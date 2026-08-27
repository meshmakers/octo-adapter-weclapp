using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappResolveSupplySources node. Reads the raw article array from
/// <c>Path</c> and the fetched <c>articleSupplySource</c> entities from
/// <see cref="SupplySourcesPath"/>, and writes the articles with their supply-source stubs
/// resolved to <c>TargetPath</c>.
/// </summary>
[NodeName("WeClappResolveSupplySources", 1)]
public record WeClappResolveSupplySourcesNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>JSONPath to the array of full <c>articleSupplySource</c> entities. Raw articles
    /// embed reference stubs only; the purchase prices the AS delivery needs live on this
    /// separate entity, which carries no article reference of its own.</summary>
    public string SupplySourcesPath { get; set; } = "";
}

/// <summary>
/// Replaces each <c>article.supplySources</c> reference stub with the full
/// <c>articleSupplySource</c> entity it points at, so a downstream renderer sees the purchase
/// prices. A stub without a matching entity is dropped and an article without stubs passes
/// through untouched, exactly as the fetch-side enrichment this node replaces did.
/// </summary>
[NodeConfiguration(typeof(WeClappResolveSupplySourcesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappResolveSupplySourcesNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappResolveSupplySourcesNodeConfiguration>();

        // Configuration guards run before anything is read or written: a definition that names
        // no path must fail visibly rather than write an empty array a renderer then skips.
        RequirePath(config.Path, nameof(config.Path));
        RequirePath(config.SupplySourcesPath, nameof(config.SupplySourcesPath));
        RequirePath(config.TargetPath, nameof(config.TargetPath));

        var articles = ReadArray(dataContext, config.Path, "article");
        var sources = ReadArray(dataContext, config.SupplySourcesPath, "articleSupplySource");

        var sourcesById = new Dictionary<string, JsonNode>();
        foreach (var source in sources.OfType<JsonObject>())
        {
            if (source["id"]?.ToString() is not { Length: > 0 } id)
            {
                continue;
            }

            if (!sourcesById.TryAdd(id, source))
            {
                throw new WeClappPipelineExecutionException(
                    $"WeClappResolveSupplySources: articleSupplySource id '{id}' appears more than once at " +
                    $"'{config.SupplySourcesPath}' - the resolution would be ambiguous");
            }
        }

        var enriched = new JsonArray();
        foreach (var article in articles)
        {
            enriched.Add(Resolve(article, sourcesById));
        }

        dataContext.Set<JsonNode>(config.TargetPath, enriched, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static JsonNode? Resolve(JsonNode? article, Dictionary<string, JsonNode> sourcesById)
    {
        var item = article?.DeepClone();
        if (item?["supplySources"]?.AsArray() is not { Count: > 0 } stubs)
        {
            return item;
        }

        var resolved = new JsonArray();
        foreach (var stub in stubs.OfType<JsonObject>())
        {
            if (stub["articleSupplySourceId"]?.ToString() is { } refId &&
                sourcesById.TryGetValue(refId, out var source))
            {
                resolved.Add(source.DeepClone());
            }
        }

        item["supplySources"] = resolved;
        return item;
    }

    private static JsonArray ReadArray(IDataContext dataContext, string path, string what)
    {
        if (dataContext.GetKind(path) != DataKind.Array)
        {
            throw new WeClappPipelineExecutionException(
                $"WeClappResolveSupplySources: no {what} array at path '{path}'");
        }

        return dataContext.Get<JsonArray>(path) ?? new JsonArray();
    }

    private static void RequirePath(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WeClappPipelineExecutionException(
                $"WeClappResolveSupplySources: '{propertyName}' must be a JSONPath");
        }
    }
}
