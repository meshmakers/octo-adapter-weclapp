using System.Text.Json;
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
/// replay-skip / reuse non-CANCELLED / createShipment), then write via the v1-required
/// GET → merge → full-PUT round trip (a partial PUT body is rejected — trial-proven
/// 2026-07-07: HTTP 400 "recipientPartyId is required"): data first with the fetched
/// version echoed, then a fresh GET and <c>status=SHIPPED</c> as the LAST write.
/// Delivered quantities are matched by articleId, never by position, and patched into
/// the fetched shipmentItems in place. Transient HTTP errors throw after retries so the
/// file stays on the SFTP server and is retried (safe: replayed shipments are skipped
/// via the SHIPPED+tracking guard).
/// </summary>
[NodeConfiguration(typeof(WeClappArWriteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappArWriteNode(
    NodeDelegate next,
    ILogger<WeClappArWriteNode> logger,
    IHttpClientFactory httpClientFactory,
    IMeshEtlContext etlContext) : IPipelineNode
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>();
        // Two dry-run signals: the standing pipeline config AND the SDK's per-execution
        // mode (a platform-initiated dry run) — either one must suppress real writes.
        var dryRun = config.DryRun || nodeContext.PipelineExecutionMode?.IsDryRun == true;
        var fileName = dataContext.Get<string>(config.FileNamePath) ?? "";
        var content = dataContext.Get<string>(config.ContentPath)
                      ?? throw new WeClappPipelineExecutionException(
                          $"WeClappArWrite: no content at '{config.ContentPath}'");

        var shipments = DilosArParser.Parse(content);
        var settings = etlContext.GlobalConfiguration.ResolveWeClappSettings(
            config.ApiConfiguration, config.BaseUrl, config.ApiKey);
        var api = new WeClappApi(httpClientFactory.CreateClient(nameof(WeClappArWriteNode)),
            settings.BaseUrl, settings.ApiKey, config.MaxRetries, config.RetryBackoffBaseSeconds);

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
                    "WeClappArWrite: sales order '{0}' (file {1}) not found in WeClapp — shipment skipped (dead-letter)",
                    ar.OrderNumber1, fileName);
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
                nodeContext.Info("WeClappArWrite: order {0} skipped — {1}", ar.OrderNumber1, plan.SkipReason ?? "");
                continue;
            }

            // 3. Resolve the target shipment id (create when none is reusable).
            string shipmentId;
            if (plan.Action == ArWriteAction.UpdateExisting)
            {
                shipmentId = plan.ExistingShipmentId!;
            }
            else
            {
                if (dryRun)
                {
                    // createShipment has no dry-run support — never create during a dry run.
                    nodeContext.Info(
                        "WeClappArWrite dry-run: would create a shipment for order {0} and mark it SHIPPED",
                        ar.OrderNumber1);
                    continue;
                }

                var createBody = WeClappApi.EnsureSuccess(
                    await api.SendAsync(HttpMethod.Post, $"salesOrder/id/{ar.OrderNumber1}/createShipment",
                        new JsonObject()),
                    $"POST createShipment for order {ar.OrderNumber1}");
                shipmentId = UnwrapObject(createBody)?["id"]?.ToString()
                             ?? throw new WeClappPipelineExecutionException(
                                 $"createShipment for order {ar.OrderNumber1} returned no shipment id");
            }

            // 4. Carrier reference. Primary: C* field 3 is the carrier id as configured in the
            //    shop system (Jürgen 2026-07-08) — for WeClapp the shippingCarrier entity id
            //    itself. Fallbacks for legacy DILOS codes: the ecommerce constant, then the
            //    carrier NAME for constant-less carriers (300 = GLS, Jürgen 2026-07-16).
            //    Tracking fields are always written; a carrier entity is never created by us.
            string? shippingCarrierId = null;
            if (plan.Update!.CarrierToken is not null || plan.Update.EcommerceShippingCarrier is not null ||
                plan.Update.CarrierName is not null)
            {
                carrierEntities ??= await LoadCarrierEntitiesAsync(api);
                if (plan.Update.CarrierToken is { } token)
                {
                    shippingCarrierId = carrierEntities
                        .FirstOrDefault(c => c["id"]?.ToString() == token)?["id"]?.ToString();
                }

                if (shippingCarrierId is null && plan.Update.EcommerceShippingCarrier is { } wantedCarrier)
                {
                    shippingCarrierId = carrierEntities
                        .FirstOrDefault(c => c["ecommerceShippingCarrier"]?.ToString() == wantedCarrier)?["id"]
                        ?.ToString();
                }

                if (shippingCarrierId is null && plan.Update.CarrierName is { } wantedName)
                {
                    shippingCarrierId = carrierEntities
                        .FirstOrDefault(c => string.Equals(c["name"]?.ToString(), wantedName,
                            StringComparison.OrdinalIgnoreCase))?["id"]?.ToString();
                }

                if (shippingCarrierId is null)
                {
                    nodeContext.Info(
                        "WeClappArWrite: no shippingCarrier entity matches token '{0}' — writing tracking without carrier reference",
                        plan.Update.CarrierToken ?? "");
                }
            }

            // 5. v1 validates every PUT body as a COMPLETE shipment (trial-proven:
            //    "recipientPartyId is required" on a partial body) → GET → merge → full PUT,
            //    then the status LADDER: a direct NEW→SHIPPED is rejected ("status update
            //    not possible", real-write-proven), the transition must step through
            //    DELIVERY_NOTE_PRINTED — one fresh GET + full PUT per missing rung.
            //    (The dry-run validation does NOT check transitions — only the real write
            //    surfaced this.)
            var dryRunSuffix = dryRun ? "?dryRun=true" : "";
            var full = await GetFullShipmentAsync(api, shipmentId);

            // Pre-order guard (trial-proven 2026-07-09): without stock createShipment returns
            // an ITEM-LESS shipment that can never reach SHIPPED ("Shipment has no items"),
            // and WeClapp derives items only at creation time. Delete the fresh empty shipment
            // (no NEW corpses piling up, one per retry) and throw — the AR file stays on the
            // SFTP and the next poll retries; after the nightly BE booked the stock, a fresh
            // createShipment derives the items (a second create next to an empty one works).
            if (plan.Action == ArWriteAction.CreateThenUpdate &&
                plan.Update.ItemQuantities.Count > 0 &&
                full["shipmentItems"]?.AsArray() is not { Count: > 0 })
            {
                await api.SendAsync(HttpMethod.Delete, $"shipment/id/{shipmentId}", null);

                // An order that already has a shipped shipment has nothing left to derive —
                // no retry can ever heal that (unlike the pre-order case). Dead-letter like
                // the unknown-order case, so the file is consumed instead of re-creating and
                // deleting a shipment on every poll.
                if (existing.Any(s => ArShipmentWritePlanner.IsShippedOrBeyond(s.Status)))
                {
                    logger.LogError(
                        "WeClappArWrite: order {OrderId} (file {FileName}) is already shipped and createShipment derived no items — dead-letter",
                        ar.OrderNumber1, fileName);
                    nodeContext.Error(
                        "WeClappArWrite: order '{0}' (file {1}) is already shipped — the AR (tracking '{2}') cannot be applied, shipment skipped (dead-letter)",
                        ar.OrderNumber1, fileName, plan.Update.PackageTrackingNumber ?? "");
                    continue;
                }

                throw new WeClappPipelineExecutionException(
                    $"Order {ar.OrderNumber1}: createShipment produced no shipment items — no stock in WeClapp yet; file stays for retry after the next stock sync");
            }

            MergeArData(full, plan.Update, shippingCarrierId, nodeContext);
            WeClappApi.EnsureSuccess(
                await api.SendAsync(HttpMethod.Put, $"shipment/id/{shipmentId}{dryRunSuffix}", full),
                $"PUT shipment {shipmentId} (data) for order {ar.OrderNumber1}");

            foreach (var rung in MissingStatusRungs(full["status"]?.ToString()))
            {
                var fresh = await GetFullShipmentAsync(api, shipmentId);
                fresh["status"] = rung;
                WeClappApi.EnsureSuccess(
                    await api.SendAsync(HttpMethod.Put, $"shipment/id/{shipmentId}{dryRunSuffix}", fresh),
                    $"PUT shipment {shipmentId} (status {rung}) for order {ar.OrderNumber1}");
            }

            nodeContext.Info(
                "WeClappArWrite: order {0} → shipment {1} {2}{3}",
                ar.OrderNumber1, shipmentId, ArShipmentUpdate.TargetStatus,
                dryRun ? " (dry-run)" : "");
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>Merges the AR field values into the fetched full shipment: scalar fields set,
    /// parcels replaced (DILOS is the source of truth for packages), item quantities patched
    /// in place by article id so every other item field survives the full-PUT round trip.</summary>
    private static void MergeArData(JsonObject shipment, ArShipmentUpdate update,
        string? shippingCarrierId, INodeContext nodeContext)
    {
        if (update.PackageTrackingNumber is not null)
        {
            shipment["packageTrackingNumber"] = update.PackageTrackingNumber;
        }

        if (update.PackageTrackingUrl is not null)
        {
            shipment["packageTrackingUrl"] = update.PackageTrackingUrl;
        }

        if (shippingCarrierId is not null)
        {
            shipment["shippingCarrierId"] = shippingCarrierId;
        }

        if (update.ShippingDateEpochMs is { } shippingDate)
        {
            shipment["shippingDate"] = shippingDate;
        }

        if (update.TotalWeight is not null)
        {
            shipment["totalWeight"] = update.TotalWeight;
        }

        // WeClapp forbids adding or removing parcels while the flat package* fields are in
        // use (live-proven 2026-07-07: HTTP 409) — so parcels are only PATCHED in place when
        // the shipment already has a matching count. Otherwise the tracking contract stays
        // on the shipment-level packageTracking* fields set above (golden reality: 102/103
        // AR shipments have exactly one parcel, so the loss is at most per-parcel weight).
        var existingParcels = shipment["parcels"]?.AsArray();
        if (update.Parcels.Count > 0 && existingParcels is { Count: > 0 })
        {
            if (existingParcels.Count == update.Parcels.Count)
            {
                for (var i = 0; i < update.Parcels.Count; i++)
                {
                    if (existingParcels[i] is not JsonObject parcelObject)
                    {
                        continue;
                    }

                    var parcel = update.Parcels[i];
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
                }
            }
            else
            {
                nodeContext.Error(
                    "WeClappArWrite: parcel count mismatch (AR {0} vs WeClapp {1}) — leaving parcels untouched, tracking stays on the shipment level",
                    update.Parcels.Count, existingParcels.Count);
            }
        }
        else if (update.Parcels.Count > 0)
        {
            nodeContext.Info(
                "WeClappArWrite: shipment has no parcels — tracking stays on the shipment level (adding parcels is rejected by WeClapp)");
        }

        var itemsArray = shipment["shipmentItems"]?.AsArray();
        var existingItems = itemsArray?.OfType<JsonNode>()
            .Select(node => node.Deserialize<WeClappShipmentItem>(CaseInsensitive)!)
            .ToList() ?? [];
        var match = ArShipmentWritePlanner.MatchItemQuantities(update, existingItems);
        LogWarnings(match.Warnings, nodeContext);

        var quantityByItemId = match.Matches.ToDictionary(m => m.ShipmentItemId, m => m.Quantity);
        foreach (var itemNode in itemsArray?.OfType<JsonObject>() ?? [])
        {
            if (itemNode["id"]?.ToString() is { } itemId &&
                quantityByItemId.TryGetValue(itemId, out var delivered))
            {
                itemNode["quantity"] = delivered;
            }
        }
    }

    /// <summary>The shipment status rungs still missing up to SHIPPED. WeClapp rejects
    /// skipping a rung (real-write-proven: NEW→SHIPPED = 400 "status update not possible"),
    /// and rungs at or below the current status must not be re-applied (a reused shipment
    /// may already be DELIVERY_NOTE_PRINTED — or SHIPPED with a different tracking number,
    /// in which case there is nothing left to set).</summary>
    internal static IReadOnlyList<string> MissingStatusRungs(string? currentStatus)
    {
        string[] ladder = ["NEW", "DELIVERY_NOTE_PRINTED", ArShipmentUpdate.TargetStatus];
        var currentIndex = Array.IndexOf(ladder, currentStatus ?? "NEW");
        // Unknown = a status outside the ladder (IN_ROUTE, DELIVERED, …): already past
        // SHIPPED — never step "back up", leave the status alone.
        return currentIndex < 0 ? [] : ladder.Skip(currentIndex + 1).ToArray();
    }

    private static async Task<JsonObject> GetFullShipmentAsync(WeClappApi api, string shipmentId)
    {
        var body = WeClappApi.EnsureSuccess(
            await api.GetAsync($"shipment/id/{shipmentId}"), $"GET shipment {shipmentId}");
        return UnwrapObject(body)
               ?? throw new WeClappPipelineExecutionException(
                   $"GET shipment {shipmentId} returned no shipment object");
    }

    /// <summary>WeClapp envelopes differ per endpoint (live-proven 2026-07-07): list GETs wrap
    /// in <c>{"result":[…]}</c>, id GETs return the bare object — accept both shapes.</summary>
    private static JsonObject? UnwrapObject(string body)
    {
        var node = JsonNode.Parse(body);
        return node?["result"] as JsonObject ?? node as JsonObject;
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
            .Select(node => node.Deserialize<WeClappShipmentSummary>(CaseInsensitive)!)
            .Where(s => s is not null)
            .ToList();
    }

    private static void LogWarnings(IReadOnlyList<string> warnings, INodeContext nodeContext)
    {
        foreach (var warning in warnings)
        {
            nodeContext.Error("WeClappArWrite: {0}", warning);
        }
    }
}
