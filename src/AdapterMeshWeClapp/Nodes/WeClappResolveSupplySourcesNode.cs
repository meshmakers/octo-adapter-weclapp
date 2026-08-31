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
/// Prepares the raw WeClapp articles for the DILOS article master delivery, doing the two things a
/// generic column renderer cannot: it drops system articles (loading equipment such as pallets),
/// which never belong in the article master; it replaces each <c>article.supplySources</c>
/// reference stub of the articles that remain with the full <c>articleSupplySource</c> entity it
/// points at (an article without stubs passes through untouched; a stub that does NOT resolve
/// fails the run, see below); and it projects the DILOS EK-Preis as a finished scalar on
/// <c>ekPreis</c>, because that value is a SELECTION - the first parseable
/// <c>supplySources[].articlePrices[].price</c>, absent meaning 0 - and a renderer can only read a
/// path. The selection and the number format itself stay in the core library next to the other
/// DILOS value rules; only the call site is here.
/// </summary>
/// <remarks>
/// For the articles that are actually delivered, every way the join can come apart fails the run
/// rather than resolving to less: an entity without an id, a stub pointing at an entity that was
/// not fetched, a supplySources value that is not an array. All of them share one consequence -
/// the article's EK-Preis falls back to 0, which is itself a LEGITIMATE value (an article without
/// a purchase price renders 0), so no downstream step and no delivered file can tell a lost price
/// from an absent one. The delivery would look complete, and it burns the per-day marker on its
/// way out, so the wrong file would stand at LKV for the whole Vienna day. A throw costs the next
/// tick and no data, which is the same trade the yaml already makes at both fetches (onHttpError:
/// Throw) and at the render (onDelimiterInValue: Fail). Live census of the customer account on
/// 2026-08-28: 48 articles, 16 articleSupplySource entities, 15 stubs, zero of them dangling - the
/// loud path is not a live-data risk today.
/// </remarks>
[NodeConfiguration(typeof(WeClappResolveSupplySourcesNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappResolveSupplySourcesNode(NodeDelegate next) : IPipelineNode
{
    private const string NodeName = "WeClappResolveSupplySources";

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappResolveSupplySourcesNodeConfiguration>();

        // Configuration guards run before anything is read or written: a definition that names
        // no path must fail visibly rather than write an empty array a renderer then skips.
        NodeConfigurationGuards.RequirePath(NodeName, config.Path, nameof(config.Path));
        NodeConfigurationGuards.RequirePath(NodeName, config.SupplySourcesPath,
            nameof(config.SupplySourcesPath));
        NodeConfigurationGuards.RequirePath(NodeName, config.TargetPath, nameof(config.TargetPath));

        var articles = ReadArray(dataContext, config.Path, "article");
        var sources = ReadArray(dataContext, config.SupplySourcesPath, "articleSupplySource");

        var sourcesById = new Dictionary<string, JsonNode>();
        for (var index = 0; index < sources.Count; index++)
        {
            // The id is the ONLY thing an article stub can point at. An entity that carries none
            // is unreachable, and the stubs aimed at it would resolve to nothing - which is
            // indistinguishable from "this article has no purchase price" once the file is
            // written. Loud here, where the entity index still exists to name.
            if ((sources[index] as JsonObject)?["id"]?.ToString() is not { Length: > 0 } id)
            {
                throw new WeClappPipelineExecutionException(
                    $"WeClappResolveSupplySources: entity {index} at '{config.SupplySourcesPath}' carries " +
                    "no 'id' - article stubs resolve against it, so its price could not be reached");
            }

            if (!sourcesById.TryAdd(id, sources[index]!))
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

            // The first of the two pieces of WeClapp knowledge a column renderer cannot express:
            // loading equipment never belongs in the article master delivery. It is dropped BEFORE
            // the join, because a system article reaches no file - so nothing about it can make a
            // delivery wrong, while joining it first turns one unresolvable stub on a pallet into
            // a blocked delivery for every tick until the WeClapp record is repaired. Reading the
            // type off the raw object is as tolerant as the model binding below: the data context
            // hands its documents out with PropertyNameCaseInsensitive.
            if (WeClappToDilos.IsSystemArticle(article["articleType"]?.ToString()))
            {
                systemArticles++;
                continue;
            }

            var resolved = Resolve(article, sourcesById, index, config);

            // Everything below reads the element AS a WeClapp article, and every way that can fail
            // - a collection carrying an explicit null, a number where the model holds a string, a
            // price shape that does not walk - surfaces from inside System.Text.Json or LINQ as a
            // raw exception naming neither this node nor the element (measured: "Value cannot be
            // null. (Parameter 'source')" for a null supplySources, a bare JsonException for a
            // numeric ean). One attribution point covers them all, including the shapes nobody has
            // met yet.
            try
            {
                var parsed = resolved.Deserialize<WeClappArticle>(CaseInsensitive)
                             ?? throw new WeClappPipelineExecutionException(
                                 $"WeClappResolveSupplySources: element {index} at '{config.Path}' is " +
                                 "not a WeClapp article");

                // The second rule: the DILOS EK-Preis is a SELECTION over the resolved supply
                // sources (first parseable price, absent means 0), which no path read performs.
                resolved["ekPreis"] = WeClappToDilos.Num(WeClappToDilos.EkPreis(parsed.PurchasePrice));
                enriched.Add(resolved);
            }
            catch (Exception ex) when (ex is not WeClappPipelineExecutionException)
            {
                throw new WeClappPipelineExecutionException(
                    $"WeClappResolveSupplySources: element {index} at '{config.Path}' is not a usable " +
                    $"WeClapp article ({ex.GetType().Name}: {ex.Message})", ex);
            }
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

    /// <summary>Replaces the article's supply-source reference stubs with the entities they point
    /// at. A stub that resolves to nothing is an error, not an omission - see the class remarks.
    /// </summary>
    private static JsonObject Resolve(JsonObject article, Dictionary<string, JsonNode> sourcesById,
        int index, WeClappResolveSupplySourcesNodeConfiguration config)
    {
        var item = (JsonObject)article.DeepClone();
        if (!item.TryGetPropertyValue("supplySources", out var value))
        {
            return item;
        }

        // An explicit JSON null means what an absent property means here - no supply sources, so
        // no price, so EK-Preis 0. It still has to be normalised rather than passed on: an
        // explicit null deserializes OVER the model's initializer, and the price walk would then
        // run against a null collection and fail as a bare ArgumentNullException naming nothing.
        if (value is null)
        {
            item["supplySources"] = new JsonArray();
            return item;
        }

        // A present-but-non-array value is a shape change, and it must not reach a raw AsArray()
        // cast: that throws an InvalidOperationException naming neither the node, the property nor
        // the element - exactly the diagnosis gap the article guard above closes.
        if (value is not JsonArray stubs)
        {
            throw new WeClappPipelineExecutionException(
                $"WeClappResolveSupplySources: element {index} at '{config.Path}' carries a " +
                $"'supplySources' value of kind {value.GetValueKind()} - an array of reference stubs " +
                "is required");
        }

        var resolved = new JsonArray();
        for (var stub = 0; stub < stubs.Count; stub++)
        {
            var refId = (stubs[stub] as JsonObject)?["articleSupplySourceId"]?.ToString();
            if (string.IsNullOrEmpty(refId) || !sourcesById.TryGetValue(refId, out var source))
            {
                throw new WeClappPipelineExecutionException(
                    $"WeClappResolveSupplySources: article '{item["id"]}' (element {index} at " +
                    $"'{config.Path}') references articleSupplySource '{refId ?? "<none>"}' in stub " +
                    $"{stub}, which is not among the {sourcesById.Count} entities at " +
                    $"'{config.SupplySourcesPath}' - its EK-Preis would silently fall back to 0");
            }

            resolved.Add(source.DeepClone());
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
}
