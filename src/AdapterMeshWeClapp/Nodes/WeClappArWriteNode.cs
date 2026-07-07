using System.Text.Json;
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
/// Configuration for the WeClappArWrite node: writes DILOS AR shipment confirmations
/// back into WeClapp (create/update shipment, tracking, parcels, delivered quantities,
/// status SHIPPED last).
/// </summary>
[NodeName("WeClappArWrite", 1)]
public record WeClappArWriteNodeConfiguration : WeClappWriteNodeConfiguration;

/// <summary>
/// Writes one DILOS AR file back into WeClapp. Per AR shipment: resolve the sales order
/// (K* Auftragsnummer1 = WeClapp salesOrder.id; 404 → dead-letter log, file is still
/// consumed), fetch existing shipments for idempotency (plan via ArShipmentWritePlanner:
/// replay-skip / reuse non-CANCELLED / createShipment), then apply two partial PUTs —
/// data first, <c>{"status":"SHIPPED"}</c> last. Delivered quantities are matched by
/// articleId, never by position; the PUT always echoes the complete shipmentItems list so
/// any replace semantics cannot drop items. Transient HTTP errors throw after retries so
/// the file stays on the SFTP server and is retried (safe: replayed shipments are skipped
/// via the SHIPPED+tracking guard). The partial-PUT shape (no version echo) is validated
/// against the trial via DryRun before go-live.
/// </summary>
[NodeConfiguration(typeof(WeClappArWriteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappArWriteNode(
    NodeDelegate next,
    ILogger<WeClappArWriteNode> logger,
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>();
        var fileName = dataContext.Get<string>(config.FileNamePath) ?? "";
        var content = dataContext.Get<string>(config.ContentPath)
                      ?? throw new WeClappPipelineExecutionException(
                          $"WeClappArWrite: no content at '{config.ContentPath}'");

        var shipments = DilosArParser.Parse(content);
        var api = new WeClappApi(httpClientFactory.CreateClient(nameof(WeClappArWriteNode)),
            config.BaseUrl, config.ApiKey, config.MaxRetries, config.RetryBackoffBaseSeconds);

        List<JsonNode>? carrierEntities = null; // looked up lazily, once per file

        foreach (var ar in shipments)
        {
            // 1. Resolve the sales order. 404 is a permanent data error: dead-letter log and
            //    continue, so the file is consumed instead of poisoning the retry loop.
            var orderResult = await api.GetAsync($"salesOrder/id/{ar.OrderNumber1}");
            if (orderResult.StatusCode == 404)
            {
                logger.LogError("WeClappArWrite: sales order {OrderId} (file {FileName}) not found — dead-letter",
                    ar.OrderNumber1, fileName);
                nodeContext.Error(
                    $"WeClappArWrite: sales order '{ar.OrderNumber1}' (file {fileName}) not found in WeClapp — shipment skipped (dead-letter)");
                continue;
            }

            WeClappApi.EnsureSuccess(orderResult, $"GET salesOrder {ar.OrderNumber1}");

            // 2. Existing shipments decide between replay-skip, reuse and create.
            var existingBody = WeClappApi.EnsureSuccess(
                await api.GetAsync($"shipment?salesOrderId-eq={ar.OrderNumber1}"),
                $"GET shipments of order {ar.OrderNumber1}");
            var existing = ParseShipmentList(existingBody);

            var plan = ArShipmentWritePlanner.Plan(ar, existing);
            LogWarnings(plan.Warnings, nodeContext);

            if (plan.Action == ArWriteAction.Skip)
            {
                nodeContext.Info($"WeClappArWrite: order {ar.OrderNumber1} skipped — {plan.SkipReason}");
                continue;
            }

            // 3. Target shipment incl. its items (for quantity matching by articleId).
            WeClappShipmentSummary target;
            if (plan.Action == ArWriteAction.UpdateExisting)
            {
                target = existing.First(s => s.Id == plan.ExistingShipmentId);
            }
            else
            {
                if (config.DryRun)
                {
                    // createShipment has no dry-run support — never create during a dry run.
                    nodeContext.Info(
                        $"WeClappArWrite dry-run: would create a shipment for order {ar.OrderNumber1} and mark it SHIPPED");
                    continue;
                }

                var createBody = WeClappApi.EnsureSuccess(
                    await api.SendAsync(HttpMethod.Post, $"salesOrder/id/{ar.OrderNumber1}/createShipment",
                        new JsonObject()),
                    $"POST createShipment for order {ar.OrderNumber1}");
                target = ParseShipment(JsonNode.Parse(createBody)?["result"])
                         ?? throw new WeClappPipelineExecutionException(
                             $"createShipment for order {ar.OrderNumber1} returned no shipment");
            }

            // 4. Carrier reference only when the DILOS code mapped AND the entity exists
            //    (V1 decision: tracking fields always, carrier id never created by us).
            string? shippingCarrierId = null;
            if (plan.Update!.EcommerceShippingCarrier is { } wantedCarrier)
            {
                carrierEntities ??= await LoadCarrierEntitiesAsync(api);
                shippingCarrierId = carrierEntities
                    .FirstOrDefault(c => c["ecommerceShippingCarrier"]?.ToString() == wantedCarrier)?["id"]
                    ?.ToString();
                if (shippingCarrierId is null)
                {
                    nodeContext.Info(
                        $"WeClappArWrite: no shippingCarrier entity for {wantedCarrier} — writing tracking without carrier reference");
                }
            }

            // 5. Two partial PUTs: data first, SHIPPED last.
            var dryRunSuffix = config.DryRun ? "?dryRun=true" : "";
            var dataBody = BuildDataBody(plan.Update, target, shippingCarrierId, nodeContext);
            WeClappApi.EnsureSuccess(
                await api.SendAsync(HttpMethod.Put, $"shipment/id/{target.Id}{dryRunSuffix}", dataBody),
                $"PUT shipment {target.Id} (data) for order {ar.OrderNumber1}");
            WeClappApi.EnsureSuccess(
                await api.SendAsync(HttpMethod.Put, $"shipment/id/{target.Id}{dryRunSuffix}",
                    new JsonObject { ["status"] = ArShipmentUpdate.TargetStatus }),
                $"PUT shipment {target.Id} (status) for order {ar.OrderNumber1}");

            nodeContext.Info(
                $"WeClappArWrite: order {ar.OrderNumber1} → shipment {target.Id} {ArShipmentUpdate.TargetStatus}"
                + (config.DryRun ? " (dry-run)" : ""));
        }

        await next(dataContext, nodeContext);
    }

    private static JsonObject BuildDataBody(ArShipmentUpdate update, WeClappShipmentSummary target,
        string? shippingCarrierId, INodeContext nodeContext)
    {
        var body = new JsonObject();
        if (update.PackageTrackingNumber is not null)
        {
            body["packageTrackingNumber"] = update.PackageTrackingNumber;
        }

        if (update.PackageTrackingUrl is not null)
        {
            body["packageTrackingUrl"] = update.PackageTrackingUrl;
        }

        if (shippingCarrierId is not null)
        {
            body["shippingCarrierId"] = shippingCarrierId;
        }

        if (update.ShippingDateEpochMs is { } shippingDate)
        {
            body["shippingDate"] = shippingDate;
        }

        if (update.TotalWeight is not null)
        {
            body["totalWeight"] = update.TotalWeight;
        }

        if (update.Parcels.Count > 0)
        {
            var parcels = new JsonArray();
            foreach (var parcel in update.Parcels)
            {
                var parcelObject = new JsonObject { ["positionNumber"] = parcel.PositionNumber };
                if (parcel.TrackingId is not null)
                {
                    parcelObject["trackingId"] = parcel.TrackingId;
                }

                if (parcel.TrackingUrl is not null)
                {
                    parcelObject["trackingUrl"] = parcel.TrackingUrl;
                }

                if (parcel.Weight is not null)
                {
                    parcelObject["weight"] = parcel.Weight;
                }

                parcels.Add(parcelObject);
            }

            body["parcels"] = parcels;
        }

        var match = ArShipmentWritePlanner.MatchItemQuantities(update, target.ShipmentItems);
        LogWarnings(match.Warnings, nodeContext);

        var quantityByItemId = match.Matches.ToDictionary(m => m.ShipmentItemId, m => m.Quantity);
        var items = new JsonArray();
        foreach (var item in target.ShipmentItems.Where(i => i.Id is not null))
        {
            // Complete list: matched items get the delivered quantity, the rest echo their
            // current one — safe regardless of WeClapp's collection replace semantics.
            var itemObject = new JsonObject { ["id"] = item.Id };
            var quantity = quantityByItemId.TryGetValue(item.Id!, out var delivered) ? delivered : item.Quantity;
            if (quantity is not null)
            {
                itemObject["quantity"] = quantity;
            }

            items.Add(itemObject);
        }

        if (items.Count > 0)
        {
            body["shipmentItems"] = items;
        }

        return body;
    }

    private static async Task<List<JsonNode>> LoadCarrierEntitiesAsync(WeClappApi api)
    {
        var body = WeClappApi.EnsureSuccess(await api.GetAsync("shippingCarrier"), "GET shippingCarrier");
        return JsonNode.Parse(body)?["result"]?.AsArray()?.OfType<JsonNode>().ToList() ?? [];
    }

    private static List<WeClappShipmentSummary> ParseShipmentList(string body)
    {
        var result = JsonNode.Parse(body)?["result"]?.AsArray()
                     ?? throw new WeClappPipelineExecutionException("shipment response has no 'result' array");
        return result.OfType<JsonNode>()
            .Select(node => ParseShipment(node)!)
            .Where(s => s is not null)
            .ToList();
    }

    private static WeClappShipmentSummary? ParseShipment(JsonNode? node) =>
        node?.Deserialize<WeClappShipmentSummary>(CaseInsensitive);

    private static void LogWarnings(IReadOnlyList<string> warnings, INodeContext nodeContext)
    {
        foreach (var warning in warnings)
        {
            nodeContext.Error($"WeClappArWrite: {warning}");
        }
    }
}
