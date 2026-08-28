using System.Text.Json;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappResolveSupplySources node. Reads the raw article array from
/// <c>Path</c> and the fetched <c>articleSupplySource</c> entities from
/// <see cref="SupplySourcesPath"/>, and writes the deliverable articles - stubs resolved, system
/// articles removed, each carrying its DILOS EK-Preis on <c>ekPreis</c> - to <c>TargetPath</c>.
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
/// Prepares the raw WeClapp articles for the DILOS article master delivery, doing the three
/// things a generic column renderer cannot: it replaces each <c>article.supplySources</c>
/// reference stub with the full <c>articleSupplySource</c> entity it points at (a stub without a
/// matching entity is dropped, an article without stubs passes through untouched); it drops
/// system articles (loading equipment such as pallets), which never belong in the article master;
/// and it projects the DILOS EK-Preis as a finished scalar on <c>ekPreis</c>, because that value
/// is a SELECTION - the first parseable <c>supplySources[].articlePrices[].price</c>, absent
/// meaning 0 - and a renderer can only read a path. The selection and the number format itself
/// stay in the core library next to the other DILOS value rules; only the call site is here.
/// </summary>
[NodeConfiguration(typeof(WeClappResolveSupplySourcesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappResolveSupplySourcesNode(NodeDelegate next) : IPipelineNode
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

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
        var systemArticles = 0;
        for (var index = 0; index < articles.Count; index++)
        {
            // WeClapp never returns a non-object element, but a mis-aimed path can - an array of
            // ids is still an array, so the array check above passes it. Measured before this
            // guard existed: a JSON null travelled on to the renderer as a phantom record, and any
            // other non-object failed inside System.Text.Json with "The node must be of type
            // 'JsonObject'" - loud, but naming neither this node nor which element.
            if (articles[index] is not JsonObject article)
            {
                throw new WeClappPipelineExecutionException(
                    $"WeClappResolveSupplySources: element {index} at '{config.Path}' is not an " +
                    "article object");
            }

            var resolved = Resolve(article, sourcesById);
            var parsed = resolved.Deserialize<WeClappArticle>(CaseInsensitive)
                         ?? throw new WeClappPipelineExecutionException(
                             $"WeClappResolveSupplySources: element {index} at '{config.Path}' is " +
                             "not a WeClapp article");

            // The two pieces of WeClapp knowledge a column renderer cannot express, applied here
            // because this step already holds the articles: loading equipment never belongs in the
            // article master delivery, and the DILOS EK-Preis is a SELECTION over the resolved
            // supply sources (first parseable price, absent means 0) which no path read performs.
            if (WeClappToDilos.IsSystemArticle(parsed))
            {
                systemArticles++;
                continue;
            }

            resolved["ekPreis"] = WeClappToDilos.Num(WeClappToDilos.EkPreis(parsed.PurchasePrice));
            enriched.Add(resolved);
        }

        if (systemArticles > 0)
        {
            nodeContext.Info("WeClappResolveSupplySources: dropped {0} system article(s) (loading equipment)",
                systemArticles);
        }

        dataContext.Set<JsonNode>(config.TargetPath, enriched, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static JsonObject Resolve(JsonObject article, Dictionary<string, JsonNode> sourcesById)
    {
        var item = (JsonObject)article.DeepClone();
        if (item["supplySources"]?.AsArray() is not { Count: > 0 } stubs)
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
