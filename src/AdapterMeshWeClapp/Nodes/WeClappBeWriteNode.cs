using System.Globalization;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.WriteBack;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappBeWrite node: reconciles DILOS BE stock snapshots into
/// WeClapp via delta movement bookings (warehouseStock is GET-only in API v1 and v2).
/// </summary>
[NodeName("WeClappBeWrite", 1)]
public record WeClappBeWriteNodeConfiguration : WeClappWriteNodeConfiguration
{
    /// <summary>WeClapp id of the warehouse that mirrors the LKV stock (customer-specific).</summary>
    public required string WarehouseId { get; set; }

    /// <summary>Page size for the bulk reads (articles, warehouseStock).</summary>
    public int PageSize { get; set; } = 500;
}

/// <summary>
/// Writes one DILOS BE file back into WeClapp as stock movement deltas. Bulk-reads the
/// warehouse (default storage place), all article ids and the warehouse's stock rows —
/// O(pages), not O(BE lines) — plans deltas via BeStockDeltaPlanner (BE quantity − current;
/// GES and unknown articles are skipped loudly), then books bookIncomingMovement /
/// bookOutgoingMovement per plan. Idempotent by construction: re-processing the same file
/// yields delta 0. DryRun logs the planned movements without posting (the movement
/// endpoints have no dry-run support).
/// </summary>
[NodeConfiguration(typeof(WeClappBeWriteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappBeWriteNode(
    NodeDelegate next,
    ILogger<WeClappBeWriteNode> logger,
    IHttpClientFactory httpClientFactory,
    IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>();
        // Two dry-run signals: the standing pipeline config AND the SDK's per-execution
        // mode (a platform-initiated dry run) — either one must suppress real bookings.
        var dryRun = config.DryRun || nodeContext.PipelineExecutionMode?.IsDryRun == true;
        var fileName = dataContext.Get<string>(config.FileNamePath) ?? "";
        var content = dataContext.Get<string>(config.ContentPath)
                      ?? throw new WeClappPipelineExecutionException(
                          $"WeClappBeWrite: no content at '{config.ContentPath}'");

        var lines = DilosBeParser.Parse(content);
        var settings = etlContext.GlobalConfiguration.ResolveWeClappSettings(
            config.ApiConfiguration, config.BaseUrl, config.ApiKey);
        var api = new WeClappApi(httpClientFactory.CreateClient(nameof(WeClappBeWriteNode)),
            settings.BaseUrl, settings.ApiKey, config.MaxRetries, config.RetryBackoffBaseSeconds);

        // Bulk reads: warehouse (booking place), article ids (key validation), stock rows.
        var warehouseBody = WeClappApi.EnsureSuccess(
            await api.GetAsync($"warehouse?id-eq={config.WarehouseId}"),
            $"GET warehouse {config.WarehouseId}");
        var warehouse = JsonNode.Parse(warehouseBody)?["result"]?.AsArray()?.FirstOrDefault()
                        ?? throw new WeClappPipelineExecutionException(
                            $"WeClappBeWrite: warehouse '{config.WarehouseId}' not found in WeClapp");
        var defaultStoragePlaceId = warehouse["defaultStoragePlaceId"]?.ToString();

        var articles = await api.GetPagedAsync("article", "properties=id,articleType", config.PageSize);
        var articleIds = articles
            .Select(a => a["id"]?.ToString())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        // Movement bookings on non-storable articles are rejected by WeClapp
        // (trial-proven 400 "article is not storable") — skip them loudly up front
        // instead of poisoning the file's retry loop.
        var storableIds = articles
            .Where(a => a["articleType"]?.ToString() == "STORABLE")
            .Select(a => a["id"]?.ToString())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var stockRows = await api.GetPagedAsync("warehouseStock",
            $"warehouseId-eq={config.WarehouseId}", config.PageSize);

        // The -eq filter is applied server-side (customer-live-verified 2026-07-10:
        // warehouseId-eq on the second warehouse returns 0 of 22 rows) — re-checked
        // locally so a foreign-warehouse row can never inflate an outgoing delta.
        // A row WITHOUT the warehouseId field is a response-contract violation and fails
        // loud: dropping it silently would read existing stock as 0 and re-book the full
        // BE quantities on top (stock inflation) on every run.
        var missingWarehouseField = stockRows.Count(row => row["warehouseId"] is null);
        if (missingWarehouseField > 0)
        {
            throw new WeClappPipelineExecutionException(
                $"WeClappBeWrite: {missingWarehouseField} of {stockRows.Count} warehouseStock rows carry no warehouseId field — refusing to plan deltas against unattributable stock");
        }

        var foreignRows = stockRows.Count(row => row["warehouseId"]!.ToString() != config.WarehouseId);
        if (foreignRows > 0)
        {
            nodeContext.Error(
                "WeClappBeWrite: {0} warehouseStock rows belong to other warehouses although the request filtered on warehouseId-eq={1} — rows excluded",
                foreignRows, config.WarehouseId);
        }

        var rowsByArticle = stockRows
            .Where(row => row["warehouseId"]!.ToString() == config.WarehouseId)
            .Select(row => (
                ArticleId: row["articleId"]?.ToString() ?? "",
                Row: new WeClappStockRow
                {
                    StoragePlaceId = row["storagePlaceId"]?.ToString() ?? "",
                    Quantity = decimal.Parse(row["quantity"]?.ToString() ?? "0", CultureInfo.InvariantCulture),
                }))
            .GroupBy(x => x.ArticleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WeClappStockRow>)g.Select(x => x.Row).ToList(),
                StringComparer.Ordinal);

        var states = new List<BeArticleState>();
        foreach (var line in lines)
        {
            if (articleIds.Contains(line.ArticleNumber) && !storableIds.Contains(line.ArticleNumber))
            {
                nodeContext.Error(
                    "WeClappBeWrite: article {0} is not storable — line skipped", line.ArticleNumber);
                continue;
            }

            states.Add(new BeArticleState
            {
                Line = line,
                // BE Artikelnummer carries the WeClapp article id (our own AS/AI echo) —
                // validated against the bulk id read, never resolved by articleNumber.
                ArticleId = articleIds.Contains(line.ArticleNumber) ? line.ArticleNumber : null,
                CurrentRows = rowsByArticle.GetValueOrDefault(line.ArticleNumber) ?? [],
                DefaultStoragePlaceId = defaultStoragePlaceId,
            });
        }

        var plan = BeStockDeltaPlanner.Plan(states, fileName);
        foreach (var warning in plan.Warnings)
        {
            logger.LogWarning("WeClappBeWrite: {Warning}", warning);
            nodeContext.Error("WeClappBeWrite: {0}", warning);
        }

        // One PERSISTENTLY rejected booking (non-transient HTTP status) must not block the
        // remaining movements: bookings already applied are delta 0 on the retry run, so
        // only the failed lines are re-attempted. Transient failures (5xx/408/429/network,
        // thrown by SendAsync after its retries) still abort the file immediately — during
        // an outage every movement would otherwise burn its own retry ladder for nothing.
        var failures = new List<string>();
        foreach (var movement in plan.Movements)
        {
            var (path, placeField) = movement.Direction == StockMovementDirection.Incoming
                ? ("warehouseStockMovement/bookIncomingMovement", "targetStoragePlaceId")
                : ("warehouseStockMovement/bookOutgoingMovement", "sourceStoragePlaceId");

            var body = new JsonObject
            {
                ["articleId"] = movement.ArticleId,
                ["quantity"] = movement.Quantity,
                ["movementNote"] = movement.MovementNote,
            };
            if (movement.StoragePlaceId is not null)
            {
                body[placeField] = movement.StoragePlaceId;
            }

            if (dryRun)
            {
                // The JSON body contains literal braces — interpolated into the message it
                // would corrupt the structured-log template, so it travels as an arg.
                nodeContext.Info("WeClappBeWrite dry-run: would POST {0} {1}", path, body.ToJsonString());
                continue;
            }

            var result = await api.SendAsync(HttpMethod.Post, path, body);
            if (!result.IsSuccess)
            {
                var failure = $"POST {path} for article {movement.ArticleId} failed with HTTP {result.StatusCode}";
                failures.Add(failure);
                nodeContext.Error("WeClappBeWrite: {0}: {1}", failure, result.Body);
                logger.LogError("WeClappBeWrite: {Failure}: {Body}", failure, result.Body);
            }
        }

        nodeContext.Info(
            "WeClappBeWrite: {0} — {1} movements{2}, {3} in sync, {4} warnings, {5} failed",
            fileName, plan.Movements.Count, dryRun ? " (dry-run)" : "",
            plan.InSyncCount, plan.Warnings.Count, failures.Count);

        if (failures.Count > 0)
        {
            // Rethrow AFTER the loop so the file stays on the SFTP server for retry while
            // every bookable movement of this run has already been applied. The response
            // bodies are already logged per movement — the exception stays bounded.
            throw new WeClappPipelineExecutionException(
                $"WeClappBeWrite: {failures.Count} of {plan.Movements.Count} movements failed for {fileName}: {string.Join("; ", failures.Take(5))}{(failures.Count > 5 ? $"; … and {failures.Count - 5} more" : "")}");
        }

        await next(dataContext, nodeContext);
    }
}
