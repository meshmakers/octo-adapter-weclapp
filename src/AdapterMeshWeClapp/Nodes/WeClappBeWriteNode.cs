using System.Globalization;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.WriteBack;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
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
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>();
        var fileName = dataContext.Get<string>(config.FileNamePath) ?? "";
        var content = dataContext.Get<string>(config.ContentPath)
                      ?? throw new WeClappPipelineExecutionException(
                          $"WeClappBeWrite: no content at '{config.ContentPath}'");

        var lines = DilosBeParser.Parse(content);
        var api = new WeClappApi(httpClientFactory.CreateClient(nameof(WeClappBeWriteNode)),
            config.BaseUrl, config.ApiKey, config.MaxRetries, config.RetryBackoffBaseSeconds);

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
        var rowsByArticle = stockRows
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

            if (config.DryRun)
            {
                // The JSON body contains literal braces — interpolated into the message it
                // would corrupt the structured-log template, so it travels as an arg.
                nodeContext.Info("WeClappBeWrite dry-run: would POST {0} {1}", path, body.ToJsonString());
                continue;
            }

            WeClappApi.EnsureSuccess(
                await api.SendAsync(HttpMethod.Post, path, body),
                $"POST {path} for article {movement.ArticleId}");
        }

        nodeContext.Info(
            "WeClappBeWrite: {0} — {1} movements{2}, {3} in sync, {4} warnings",
            fileName, plan.Movements.Count, config.DryRun ? " (dry-run)" : "",
            plan.InSyncCount, plan.Warnings.Count);

        await next(dataContext, nodeContext);
    }
}
