using System.Text.Json.Nodes;
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
    /// <summary>WeClapp API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".</summary>
    public required string BaseUrl { get; set; }

    /// <summary>WeClapp API token (sent as "AuthenticationToken" header). Comes from the
    /// pipeline deployment configuration — never hardcode or log it.</summary>
    public required string ApiKey { get; set; }

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
    IHttpClientFactory httpClientFactory) : ITriggerPipelineNode
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

    /// <inheritdoc />
    public Task StartAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        context.NodeContext.Info($"WeClappFetch: polling '{config.Entity}' every {config.PollingIntervalSeconds}s");

        _pollingTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await FetchOnceAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and keep polling — a single failed poll must not kill the trigger.
                    logger.LogError(ex, "WeClappFetch: poll for '{Entity}' failed", config.Entity);
                    context.NodeContext.Error($"WeClappFetch poll failed: {ex.Message}");
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
    /// Internal so tests can drive it directly without the polling loop.</summary>
    internal async Task FetchOnceAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>();
        var http = httpClientFactory.CreateClient(nameof(WeClappFetchTriggerNode));

        switch (config.Entity)
        {
            case "article":
                await FetchArticlesAsync(http, config, context);
                break;

            case "salesOrder":
                await FetchOrdersAsync(http, config, context);
                break;

            default:
                throw new WeClappPipelineExecutionException(
                    $"Unknown WeClappFetch entity '{config.Entity}' (expected 'article' or 'salesOrder')");
        }
    }

    private static async Task FetchArticlesAsync(HttpClient http, WeClappFetchTriggerNodeConfiguration config,
        ITriggerContext context)
    {
        var articles = await FetchAllPagesAsync(http, config, "article", config.AdditionalQuery);

        // EK enrichment: raw articles embed only supply-source REFERENCE STUBS
        // ({articleSupplySourceId}); the purchase prices live on the separate
        // articleSupplySource entity, which has no articleId of its own
        // (customer-verified 2026-07-08). One entity fetch per poll resolves the stubs.
        Dictionary<string, JsonNode>? sourcesById = null;
        if (articles.Any(a => a["supplySources"]?.AsArray() is { Count: > 0 }))
        {
            var sources = await FetchAllPagesAsync(http, config, "articleSupplySource", "");
            sourcesById = sources
                .Where(s => s["id"] is not null)
                .ToDictionary(s => s["id"]!.ToString(), s => s);
        }

        foreach (var article in articles)
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

            var document = new JsonObject { ["item"] = item };
            await ExecutePipelineAsync(context, document);
        }
    }

    private static async Task FetchOrdersAsync(HttpClient http, WeClappFetchTriggerNodeConfiguration config,
        ITriggerContext context)
    {
        // orderItems are included in the default salesOrder response (live-verified).
        var orders = await FetchAllPagesAsync(http, config, "salesOrder", config.AdditionalQuery);

        var customerCache = new Dictionary<string, JsonNode?>();

        foreach (var order in orders)
        {
            var customerId = order["customerId"]?.ToString() ?? "";
            JsonNode? customer = null;
            if (customerId.Length > 0)
            {
                if (!customerCache.TryGetValue(customerId, out customer))
                {
                    var matches = await FetchAllPagesAsync(http, config, "customer", $"id-eq={customerId}");
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
        WeClappFetchTriggerNodeConfiguration config, string entity, string additionalQuery)
    {
        var results = new List<JsonNode>();
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var page = 1;

        while (true)
        {
            var url = $"{baseUrl}/{entity}?page={page}&pageSize={config.PageSize}"
                      + (additionalQuery.Length > 0 ? "&" + additionalQuery : "");

            var json = await GetWithRetryAsync(http, url, config);
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
        WeClappFetchTriggerNodeConfiguration config)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("AuthenticationToken", config.ApiKey);
                using var response = await http.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                var status = (int)response.StatusCode;
                var errorBody = Truncate(await response.Content.ReadAsStringAsync(), 300);
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

            if (attempt < config.MaxRetries && config.RetryBackoffBaseSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(
                    config.RetryBackoffBaseSeconds * Math.Pow(2, attempt - 1)));
            }
        }

        throw new WeClappPipelineExecutionException(
            $"WeClapp request failed after {config.MaxRetries} attempts ({lastError}) for {url}");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
