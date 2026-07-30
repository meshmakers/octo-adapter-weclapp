using System.Globalization;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappFetch trigger node (custom node #1 of the ingestion design).
/// API facts verified 2026-06-23 (scaffold) + officially confirmed auth header:
/// base https://{tenant}.weclapp.com/webapp/api/v1, header "AuthenticationToken",
/// paging via page/pageSize until an empty page, filter vocabulary -eq/-ne/-in.
/// </summary>
[NodeName("WeClappFetch", 1)]
public record WeClappFetchTriggerNodeConfiguration : TriggerNodeConfiguration
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
    /// their customer; orderItems are part of the default salesOrder response — live-verified,
    /// an additionalProperties parameter does NOT exist and yields HTTP 400).</summary>
    public required string Entity { get; set; }

    /// <summary>Optional additional query, e.g. "status-eq=ORDER_ENTRY_IN_PROGRESS".</summary>
    public string AdditionalQuery { get; set; } = "";

    /// <summary>Page size for the paging loop (WeClapp default limit applies server-side).</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Seconds between polls (design: articles daily, orders every 15 min — configure per pipeline).</summary>
    public int PollingIntervalSeconds { get; set; } = 900;

    /// <summary>When false, the polling loop delays FIRST and fetches only after the first
    /// interval — a (re)deploy then never triggers an immediate fetch/delivery (P2
    /// redeploy determinism; the as pipeline sets false). Default true keeps the
    /// fetch-first behavior for idempotent pipelines (ck) and gated ones (ai).</summary>
    public bool RunOnStart { get; set; } = true;

    /// <summary>How fetched documents start pipeline executions: "PerItem" (default — one
    /// execution per document, the AI/CK shape) or "Batch" (one execution per poll shaped
    /// <c>{ "items": [ … ] }</c> — the AS collector shape; golden precedent is ONE AS file
    /// per run with all articles). Batch is only valid for entity "article".</summary>
    public string EmitMode { get; set; } = "PerItem";

    /// <summary>Resolve supply-source reference stubs into full articleSupplySource entities
    /// (EK prices; one extra entity fetch per poll). The AS delivery needs them (EK-Preis
    /// field); pipelines that do not read prices (CK sync) set false and skip the fetch.</summary>
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
/// Pulls WeClapp entities page by page and starts one pipeline execution per document:
/// articles as <c>{ "item": … }</c>, sales orders as <c>{ "item": …, "customer": … }</c>
/// (customer joined via the verified <c>id-eq</c> filter and cached per poll) — exactly the
/// shape the downstream WeClappToCk node expects. Polling loop per FromMicrosoftGraphNode
/// precedent: errors are logged and the next interval retries; transient HTTP errors are
/// retried with exponential backoff (Billbee ApiCallWithRetry analog).
/// </summary>
[NodeConfiguration(typeof(WeClappFetchTriggerNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappFetchTriggerNode(
    ILogger<WeClappFetchTriggerNode> logger,
    IHttpClientFactory httpClientFactory,
    TimeProvider? timeProvider = null) : ITriggerPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

    /// <inheritdoc />
    public Task StartAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        context.NodeContext.Info("WeClappFetch: polling '{0}' every {1}s", config.Entity, config.PollingIntervalSeconds);
        if (!config.RunOnStart)
        {
            context.NodeContext.Info("WeClappFetch: first poll delayed by {0}s (runOnStart=false)",
                config.PollingIntervalSeconds);
        }

        _pollingTask = Task.Run(async () =>
        {
            if (!config.RunOnStart)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await FetchOnceAsync(context, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and keep polling — a single failed poll must not kill the trigger.
                    logger.LogError(ex, "WeClappFetch: poll for '{Entity}' failed", config.Entity);
                    context.NodeContext.Error("WeClappFetch poll failed: {0}", ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(ITriggerContext context)
    {
        _cancellationTokenSource?.Cancel();

        if (_pollingTask is { } task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // expected on cancel
            }
        }

        context.NodeContext.Info("WeClappFetch: polling stopped");
    }

    /// <summary>One poll: fetch all pages and start one pipeline execution per document.
    /// Internal so tests can drive it directly without the polling loop. The cancellation
    /// token (the trigger's stop token) reaches every HTTP call, so a hanging request
    /// does not block adapter shutdown.</summary>
    internal async Task FetchOnceAsync(ITriggerContext context, CancellationToken cancellationToken = default)
    {
        var config = context.NodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>();

        if (config.EmitMode is not ("PerItem" or "Batch"))
        {
            throw new WeClappPipelineExecutionException(
                $"Unknown WeClappFetch emitMode '{config.EmitMode}' (expected 'PerItem' or 'Batch')");
        }

        if (config.EmitMode == "Batch" && config.Entity != "article")
        {
            throw new WeClappPipelineExecutionException(
                "WeClappFetch emitMode 'Batch' is only supported for entity 'article' — " +
                "orders stay per-item (one golden AI file per order)");
        }

        var settings = context.GlobalConfiguration.ResolveWeClappSettings(
            config.ApiConfiguration, config.BaseUrl, config.ApiKey);

        var http = httpClientFactory.CreateClient(nameof(WeClappFetchTriggerNode));

        switch (config.Entity)
        {
            case "article":
                await FetchArticlesAsync(http, config, settings, context, _timeProvider, cancellationToken);
                break;

            case "salesOrder":
                await FetchOrdersAsync(http, config, settings, context, cancellationToken);
                break;

            default:
                throw new WeClappPipelineExecutionException(
                    $"Unknown WeClappFetch entity '{config.Entity}' (expected 'article' or 'salesOrder')");
        }
    }

    private static async Task FetchArticlesAsync(HttpClient http, WeClappFetchTriggerNodeConfiguration config,
        WeClappConnectionSettings settings, ITriggerContext context, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var articles = await FetchAllPagesAsync(http, config, settings, "article", config.AdditionalQuery,
            cancellationToken);

        // EK enrichment: raw articles embed only supply-source REFERENCE STUBS
        // ({articleSupplySourceId}); the purchase prices live on the separate
        // articleSupplySource entity, which has no articleId of its own
        // (customer-verified 2026-07-08). One entity fetch per poll resolves the stubs.
        Dictionary<string, JsonNode>? sourcesById = null;
        if (config.EnrichSupplySources && articles.Any(a => a["supplySources"]?.AsArray() is { Count: > 0 }))
        {
            var sources = await FetchAllPagesAsync(http, config, settings, "articleSupplySource", "",
                cancellationToken);
            sourcesById = sources
                .Where(s => s["id"] is not null)
                .ToDictionary(s => s["id"]!.ToString(), s => s);
        }

        JsonNode Enrich(JsonNode article)
        {
            var item = article.DeepClone();
            if (sourcesById is not null && item["supplySources"]?.AsArray() is { Count: > 0 } stubs)
            {
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
            }

            return item;
        }

        if (config.EmitMode == "Batch")
        {
            // One execution per poll carrying ALL articles — the AS collector shape
            // (golden precedent: one AS file per run). Zero articles → no execution,
            // an empty AS upload would be a false "no articles exist" snapshot.
            if (articles.Count == 0)
            {
                return;
            }

            var items = new JsonArray();
            foreach (var article in articles)
            {
                items.Add(Enrich(article));
            }

            var viennaNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), ViennaTime.Zone);
            await ExecutePipelineAsync(context, new JsonObject
            {
                ["items"] = items,
                ["meta"] = new JsonObject
                {
                    ["exportKind"] = config.ExportKind,
                    ["exportDate"] = viennaNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
            });
            return;
        }

        foreach (var article in articles)
        {
            var document = new JsonObject { ["item"] = Enrich(article) };
            await ExecutePipelineAsync(context, document);
        }
    }

    private static async Task FetchOrdersAsync(HttpClient http, WeClappFetchTriggerNodeConfiguration config,
        WeClappConnectionSettings settings, ITriggerContext context, CancellationToken cancellationToken)
    {
        // orderItems are included in the default salesOrder response (live-verified).
        var orders = await FetchAllPagesAsync(http, config, settings, "salesOrder", config.AdditionalQuery,
            cancellationToken);

        var customerCache = new Dictionary<string, JsonNode?>();

        foreach (var order in orders)
        {
            var customerId = order["customerId"]?.ToString() ?? "";
            JsonNode? customer = null;
            if (customerId.Length > 0)
            {
                if (!customerCache.TryGetValue(customerId, out customer))
                {
                    var matches = await FetchAllPagesAsync(http, config, settings, "customer", $"id-eq={customerId}",
                        cancellationToken);
                    customer = matches.FirstOrDefault();
                    customerCache[customerId] = customer;
                }
            }

            var document = new JsonObject
            {
                ["item"] = order.DeepClone(),
                ["customer"] = customer?.DeepClone(),
            };
            await ExecutePipelineAsync(context, document);
        }
    }

    private static async Task ExecutePipelineAsync(ITriggerContext context, JsonObject document)
    {
        await context.ExecuteAsync(
            new ExecutePipelineOptions(DateTime.UtcNow) { ExternalReceivedDateTime = DateTime.UtcNow },
            document);
    }

    private static async Task<List<JsonNode>> FetchAllPagesAsync(HttpClient http,
        WeClappFetchTriggerNodeConfiguration config, WeClappConnectionSettings settings, string entity,
        string additionalQuery, CancellationToken cancellationToken)
    {
        var results = new List<JsonNode>();
        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var page = 1;

        while (true)
        {
            var url = $"{baseUrl}/{entity}?page={page}&pageSize={config.PageSize}"
                      + (additionalQuery.Length > 0 ? "&" + additionalQuery : "");

            var json = await GetWithRetryAsync(http, url, config, settings, cancellationToken);
            var result = JsonNode.Parse(json)?["result"]?.AsArray()
                         ?? throw new WeClappPipelineExecutionException(
                             $"WeClapp response for '{entity}' page {page} has no 'result' array");

            if (result.Count == 0)
            {
                break;
            }

            results.AddRange(result.OfType<JsonNode>());

            if (result.Count < config.PageSize)
            {
                break; // short page — the next one is guaranteed empty
            }

            page++;
        }

        return results;
    }

    private static async Task<string> GetWithRetryAsync(HttpClient http, string url,
        WeClappFetchTriggerNodeConfiguration config, WeClappConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        string? lastError = null;
        var attempts = Math.Max(1, config.MaxRetries); // a misconfigured 0 must still try once

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("AuthenticationToken", settings.ApiKey);
                using var response = await http.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }

                var status = (int)response.StatusCode;
                var errorBody = Truncate(await response.Content.ReadAsStringAsync(cancellationToken), 300);
                var transient = status >= 500 || status == 408 || status == 429;
                if (!transient)
                {
                    throw new WeClappPipelineExecutionException(
                        $"WeClapp request failed with HTTP {status} for {url}: {errorBody}");
                }

                lastError = $"HTTP {status}: {errorBody}";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            if (attempt < attempts && config.RetryBackoffBaseSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(
                    config.RetryBackoffBaseSeconds * Math.Pow(2, attempt - 1)), cancellationToken);
            }
        }

        throw new WeClappPipelineExecutionException(
            $"WeClapp request failed after {attempts} attempts ({lastError}) for {url}");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Never split a UTF-16 surrogate pair at the cut.
        if (char.IsHighSurrogate(value[maxLength - 1]))
        {
            maxLength--;
        }

        return value[..maxLength];
    }
}
