using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappFetchStep transform node — the step-node counterpart of
/// <see cref="WeClappFetchTriggerNodeConfiguration"/> for the cron-trigger redesign
/// (AB#4228/G2): same fetch surface, minus the polling-only fields
/// (<c>PollingIntervalSeconds</c>, <c>RunOnStart</c>) that make no sense once a platform
/// cron trigger (<c>FromPipelineTriggerEvent@1</c>) drives execution instead of a poll loop.
/// </summary>
[NodeName("WeClappFetchStep", 1)]
public record WeClappFetchStepNodeConfiguration : NodeConfiguration, IWeClappFetchConfiguration
{
    /// <summary>Name of the tenant GlobalConfiguration entry with the WeClapp access settings
    /// ({ baseUrl, apiKey }, e.g. "WeClappApi" — shared with the write-back nodes). When set,
    /// it takes precedence over the inline <see cref="BaseUrl"/>/<see cref="ApiKey"/>; the key
    /// then lives once per tenant instead of in every pipeline definition.</summary>
    public string? ApiConfiguration { get; set; }

    /// <summary>WeClapp API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".
    /// Optional when <see cref="ApiConfiguration"/> is set.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>WeClapp API token (sent as "AuthenticationToken" header) — never hardcode or
    /// log it. Optional when <see cref="ApiConfiguration"/> is set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>WeClapp entity to pull: "article" or "salesOrder" (orders are joined with
    /// their customer; orderItems are part of the default salesOrder response).</summary>
    public required string Entity { get; set; }

    /// <summary>Optional additional query, e.g. "status-eq=ORDER_ENTRY_IN_PROGRESS".</summary>
    public string AdditionalQuery { get; set; } = "";

    /// <summary>Page size for the paging loop (WeClapp default limit applies server-side).</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>How the fetch result is shaped: "PerItem" (default — <c>$.articles</c> /
    /// <c>$.orders</c>, one <c>{ item, ... }</c>-wrapped element per document, the ck/ai
    /// shape for a downstream <c>ForEach@1</c>) or "Batch" (<c>$.items</c> + <c>$.meta</c>,
    /// the AS collector shape). Batch is only valid for entity "article".</summary>
    public string EmitMode { get; set; } = "PerItem";

    /// <summary>Resolve supply-source reference stubs into full articleSupplySource entities
    /// (EK prices; one extra entity fetch per run). Pipelines that do not read prices (CK
    /// sync) set false and skip the fetch.</summary>
    public bool EnrichSupplySources { get; set; } = true;

    /// <summary>Marker kind emitted as $.meta.exportKind in Batch mode — the delivery-dedup
    /// gate keys its per-day CK marker (Industry.Logistics/ExportRun) on it. Only used
    /// with emitMode Batch.</summary>
    public string ExportKind { get; set; } = "AS";

    /// <summary>Retry attempts for transient HTTP failures (5xx/408/429/network), exponential backoff.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Backoff base in seconds (delay = base * 2^(attempt-1)); tests set 0.</summary>
    public double RetryBackoffBaseSeconds { get; set; } = 1;
}

/// <summary>
/// Fetches WeClapp entities and seeds the data context with them at a fixed root path, for a
/// platform cron trigger (<c>FromPipelineTriggerEvent@1</c>) to drive instead of the legacy
/// <see cref="WeClappFetchTriggerNode"/> poll loop (AB#4228/G2 cron-trigger redesign). Per-item
/// modes hand the seeded array to a downstream <c>ForEach@1</c>; Batch hands it straight to the
/// AS render chain. Root shapes (ALWAYS seeded, even empty — a missing/non-array path aborts a
/// downstream <c>ForEach@1</c> with <c>PathMustBeArray</c>; an empty array no-ops):
/// <list type="bullet">
/// <item>entity article, emitMode Batch → <c>$.items</c> (enriched array) + <c>$.meta</c>
/// (<c>exportKind</c>, <c>exportDate</c> in Vienna local date) — byte-identical to the legacy
/// trigger's shape.</item>
/// <item>entity salesOrder → <c>$.orders</c> = array of <c>{ item, customer }</c> (customer
/// joined via the <c>id-eq</c> filter and cached per run).</item>
/// <item>entity article, emitMode PerItem → <c>$.articles</c> = array of <c>{ item }</c> —
/// wrapped exactly like the legacy trigger's per-execution document.</item>
/// </list>
/// </summary>
[NodeConfiguration(typeof(WeClappFetchStepNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappFetchStepNode(
    NodeDelegate next,
    IHttpClientFactory httpClientFactory,
    IMeshEtlContext etlContext,
    ILogger<WeClappFetchStepNode> logger,
    TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappFetchStepNodeConfiguration>();

        if (config.EmitMode is not ("PerItem" or "Batch"))
        {
            throw new WeClappPipelineExecutionException(
                $"Unknown WeClappFetchStep emitMode '{config.EmitMode}' (expected 'PerItem' or 'Batch')");
        }

        if (config.EmitMode == "Batch" && config.Entity != "article")
        {
            throw new WeClappPipelineExecutionException(
                "WeClappFetchStep emitMode 'Batch' is only supported for entity 'article' — " +
                "orders stay per-item (one golden AI file per order)");
        }

        // Reuse the trigger's client NAME: gzip AutomaticDecompression is registered per-name
        // in Program.cs — a new name here would silently lose decompression for WeClapp's
        // gzip-compressed responses and only fail on staging.
        var http = httpClientFactory.CreateClient(nameof(WeClappFetchTriggerNode));

        // Same resolution as the legacy trigger and the write-back nodes: a tenant
        // GlobalConfiguration entry (apiConfiguration) wins over inline baseUrl/apiKey,
        // and a half-configured entry fails loud instead of silently falling back.
        var settings = etlContext.GlobalConfiguration.ResolveWeClappSettings(
            config.ApiConfiguration, config.BaseUrl, config.ApiKey);

        switch (config.Entity)
        {
            case "article":
                await FetchArticlesAsync(http, config, settings, dataContext, _timeProvider);
                break;

            case "salesOrder":
                await FetchOrdersAsync(http, config, settings, dataContext);
                break;

            default:
                throw new WeClappPipelineExecutionException(
                    $"Unknown WeClappFetchStep entity '{config.Entity}' (expected 'article' or 'salesOrder')");
        }

        logger.LogDebug("WeClappFetchStep: fetched entity '{Entity}' (emitMode {EmitMode})",
            config.Entity, config.EmitMode);

        await next(dataContext, nodeContext);
    }

    private static async Task FetchArticlesAsync(HttpClient http, WeClappFetchStepNodeConfiguration config,
        WeClappConnectionSettings settings, IDataContext dataContext, TimeProvider timeProvider)
    {
        var articles = await WeClappFetchCore.FetchEnrichedArticlesAsync(http, config, settings,
            config.AdditionalQuery, CancellationToken.None);

        if (config.EmitMode == "Batch")
        {
            var (items, meta) = WeClappFetchCore.BuildBatchDocumentParts(articles, config.ExportKind, timeProvider);

            dataContext.Set("$.items", items, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
            dataContext.Set("$.meta", meta, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
            return;
        }

        var wrapped = new JsonArray();
        foreach (var article in articles)
        {
            wrapped.Add(new JsonObject { ["item"] = article });
        }

        dataContext.Set("$.articles", wrapped, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
    }

    private static async Task FetchOrdersAsync(HttpClient http, WeClappFetchStepNodeConfiguration config,
        WeClappConnectionSettings settings, IDataContext dataContext)
    {
        var orders = await WeClappFetchCore.FetchOrdersWithCustomersAsync(http, config, settings,
            config.AdditionalQuery, CancellationToken.None);

        var wrapped = new JsonArray();
        foreach (var (order, customer) in orders)
        {
            wrapped.Add(new JsonObject { ["item"] = order, ["customer"] = customer });
        }

        dataContext.Set("$.orders", wrapped, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
    }
}
