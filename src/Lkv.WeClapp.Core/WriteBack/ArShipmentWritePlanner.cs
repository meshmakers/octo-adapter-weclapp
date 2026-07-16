using System.Globalization;
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.WriteBack;

/// <summary>
/// Pure planning logic for writing one DILOS AR shipment back into WeClapp. No I/O:
/// the caller supplies the parsed AR shipment plus the already-fetched WeClapp shipments
/// of the sales order and executes the returned plan (create via
/// POST /salesOrder/id/{id}/createShipment, apply via PUT /shipment/id/{id}, set
/// status SHIPPED last). Matching of delivered quantities is by WeClapp article id —
/// never by DILOS position, whose echo semantics are unverified (see DilosArItem).
/// </summary>
public static class ArShipmentWritePlanner
{
    public static ArWritePlan Plan(DilosArShipment ar, IReadOnlyList<WeClappShipmentSummary> existingShipments)
    {
        var warnings = new List<string>();
        var update = BuildUpdate(ar, warnings);

        // Replay protection: a shipment at or beyond SHIPPED carrying the same tracking
        // number means this AR file was processed before (e.g. remote delete failed).
        // WeClapp advances shipments past SHIPPED, so the comparison must not be literal.
        var replay = existingShipments.FirstOrDefault(s =>
            IsShippedOrBeyond(s.Status) &&
            update.PackageTrackingNumber is not null &&
            s.PackageTrackingNumber == update.PackageTrackingNumber);
        if (replay is not null)
        {
            return new ArWritePlan
            {
                Action = ArWriteAction.Skip,
                SkipReason = $"Shipment {replay.Id} is already {replay.Status} with tracking number {update.PackageTrackingNumber}",
                Warnings = warnings
            };
        }

        // Untracked replay signature: an untracked AR that was processed before left a
        // shipped shipment WITHOUT a tracking number behind — only that exact signature
        // is treated as a replay. A shipped shipment WITH tracking does not block an
        // untracked AR (that would be a genuinely new delivery, written below).
        var untrackedReplay = existingShipments.FirstOrDefault(s =>
            update.PackageTrackingNumber is null &&
            IsShippedOrBeyond(s.Status) &&
            s.PackageTrackingNumber is null);
        if (untrackedReplay is not null)
        {
            return new ArWritePlan
            {
                Action = ArWriteAction.Skip,
                SkipReason = $"Shipment {untrackedReplay.Id} is already {untrackedReplay.Status} without a tracking number — treated as a replay of this untracked AR",
                Warnings = warnings
            };
        }

        // Pre-order scenario (trial-proven 2026-07-09): an ITEM-LESS shipment (createShipment
        // without stock) can never reach SHIPPED and items are only derived at creation time —
        // never reuse it when the AR wants item quantities; a fresh createShipment next to it
        // derives the items once stock arrived. Shipments at or beyond SHIPPED are never
        // reused either: a completed delivery's record must not be overwritten — a second
        // delivery gets its own shipment (never observed in golden files or the customer's
        // 87 live orders, but WeClapp permits it).
        var reusable = existingShipments.FirstOrDefault(s =>
            s.Status != "CANCELLED" &&
            !IsShippedOrBeyond(s.Status) &&
            (s.ShipmentItems.Count > 0 || update.ItemQuantities.Count == 0));
        if (reusable is not null)
        {
            return new ArWritePlan
            {
                Action = ArWriteAction.UpdateExisting,
                ExistingShipmentId = reusable.Id,
                Update = update,
                Warnings = warnings
            };
        }

        return new ArWritePlan
        {
            Action = ArWriteAction.CreateThenUpdate,
            Update = update,
            Warnings = warnings
        };
    }

    /// <summary>Statuses at or beyond SHIPPED. WeClapp advances shipments past SHIPPED
    /// (e.g. DELIVERED), so replay and reuse decisions must not compare literally;
    /// CANCELLED is the only terminal status that does NOT mean "delivered", and a
    /// missing/empty status means the shipment has not shipped, never the opposite.</summary>
    public static bool IsShippedOrBeyond(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        status is not ("CANCELLED" or "NEW" or "DELIVERY_NOTE_PRINTED");

    /// <summary>
    /// Matches planned delivered quantities onto concrete WeClapp shipment items by article id.
    /// Unmatched entries on either side are reported as warnings, never guessed. Every shipment
    /// item is matched at most once — callers key the result by ShipmentItemId, so a duplicate
    /// would be fatal there. The planner aggregates duplicate articles upstream (BuildUpdate);
    /// the at-most-once guard here is a structural backstop for hand-built updates, where the
    /// first quantity wins.
    /// </summary>
    public static ItemMatchResult MatchItemQuantities(
        ArShipmentUpdate update, IReadOnlyList<WeClappShipmentItem> shipmentItems)
    {
        var matches = new List<ItemQuantityMatch>();
        var warnings = new List<string>();
        var matchedShipmentItemIds = new HashSet<string>();

        foreach (var wanted in update.ItemQuantities)
        {
            var target = shipmentItems.FirstOrDefault(si => si.ArticleId == wanted.ArticleId);
            if (target?.Id is null)
            {
                warnings.Add($"AR item with article {wanted.ArticleId} has no matching WeClapp shipment item");
                continue;
            }

            if (shipmentItems.Count(si => si.ArticleId == wanted.ArticleId) > 1)
            {
                // The AR states one delivered total per article; how it splits across
                // duplicate-article shipment items is unknowable from the file. The total
                // lands on the first item, the others keep their quantities — loud, so an
                // overstated shipment total is traceable to this decision.
                warnings.Add(
                    $"Article {wanted.ArticleId}: the shipment has multiple items with this article — the delivered total {wanted.Quantity} is written to item {target.Id} only, the others keep their quantities");
            }

            if (!matchedShipmentItemIds.Add(target.Id))
            {
                warnings.Add(
                    $"AR item with article {wanted.ArticleId} resolves to shipment item {target.Id}, which is already matched — quantity {wanted.Quantity} not applied");
                continue;
            }

            matches.Add(new ItemQuantityMatch { ShipmentItemId = target.Id, Quantity = wanted.Quantity });
        }

        foreach (var untouched in shipmentItems.Where(si => si.Id is not null && !matchedShipmentItemIds.Contains(si.Id)))
        {
            warnings.Add($"WeClapp shipment item {untouched.Id} (article {untouched.ArticleId}) not covered by AR file");
        }

        return new ItemMatchResult { Matches = matches, Warnings = warnings };
    }

    private static ArShipmentUpdate BuildUpdate(DilosArShipment ar, List<string> warnings)
    {
        var parcels = new List<WeClappParcelWrite>();
        for (var i = 0; i < ar.Parcels.Count; i++)
        {
            var p = ar.Parcels[i];
            var tracking = TrackingSplitter.Split(p.TrackingNumber);
            if (tracking.Numbers.Count > 1)
            {
                warnings.Add($"Parcel {i + 1}: {tracking.Numbers.Count} distinct tracking numbers in '{p.TrackingNumber}' — keeping all, using first");
            }

            parcels.Add(new WeClappParcelWrite
            {
                PositionNumber = i + 1,
                TrackingId = tracking.Numbers.Count > 0 ? tracking.Numbers[0] : null,
                TrackingUrl = tracking.Url,
                Weight = Dec(p.Weight)
            });
        }

        // Shipment-level tracking/carrier come from the first parcel that actually carries
        // a tracking number (fallback: the first parcel) — an untracked first parcel must
        // not blank the shipment-level fields, the replay guards key on them.
        var primaryIndex = parcels.FindIndex(parcel => parcel.TrackingId is not null);
        if (primaryIndex < 0 && parcels.Count > 0)
        {
            primaryIndex = 0;
        }

        string? shipmentTrackingNumber = null;
        string? shipmentTrackingUrl = null;
        string? carrierToken = null;
        string? carrier = null;
        string? carrierName = null;
        if (primaryIndex >= 0)
        {
            shipmentTrackingNumber = parcels[primaryIndex].TrackingId;
            shipmentTrackingUrl = parcels[primaryIndex].TrackingUrl;
            // The token may be a WeClapp shippingCarrier entity id (primary contract since
            // 2026-07-08) — only the node can judge that against the live carrier list, so
            // no warning here; the legacy code map is attached as a fallback.
            var rawCarrier = ar.Parcels[primaryIndex].Carrier;
            carrierToken = rawCarrier.Trim().Length > 0 ? rawCarrier.Trim() : null;
            if (DilosCarrierMap.TryMap(rawCarrier, out var mapped))
            {
                carrier = mapped;
            }
            else if (DilosCarrierMap.TryMapName(rawCarrier, out var mappedName))
            {
                carrierName = mappedName;
            }
        }

        if (ar.Difference == "2")
        {
            warnings.Add("DILOS Differenzen=2: shortages exist and are not backordered by DILOS — writing delivered quantities only");
        }

        // Delivered quantities are aggregated per article: DILOS may echo the same article
        // on several AR lines (WeClapp allows duplicate-article order positions, and a
        // position can be split across parcels), while the write path keys quantities by
        // articleId → shipmentItemId — a duplicate key must never leave the planner.
        // Never observed in golden files (0/103 shipments) or the customer's live orders
        // (0/87); GroupBy keeps first-occurrence order.
        var itemQuantities = new List<ArItemQuantity>();
        foreach (var group in ar.Items
                     .Where(item => item.ArticleNumber.Length > 0) // empty = shipping-cost pseudo item
                     .GroupBy(item => item.ArticleNumber, StringComparer.Ordinal))
        {
            var lines = group.ToList();
            foreach (var item in lines)
            {
                if (item.OpenQuantity is < 0)
                {
                    warnings.Add($"Article {item.ArticleNumber}: over-delivery (open quantity {item.OpenQuantity}) — passing delivered quantity through");
                }
            }

            if (lines.Count > 1)
            {
                warnings.Add($"Article {group.Key}: appears on {lines.Count} AR lines — delivered quantities summed");
            }

            itemQuantities.Add(new ArItemQuantity
            {
                ArticleId = group.Key,
                Quantity = Dec(lines.Sum(item => item.DeliveredQuantity))!
            });
        }

        return new ArShipmentUpdate
        {
            PackageTrackingNumber = shipmentTrackingNumber,
            PackageTrackingUrl = shipmentTrackingUrl,
            ShippingDateEpochMs = ar.ShipmentDate is { } d
                ? ViennaTime.ToEpochMsAtViennaMidnight(d)
                : null,
            TotalWeight = Dec(ar.TotalWeight),
            CarrierToken = carrierToken,
            EcommerceShippingCarrier = carrier,
            CarrierName = carrierName,
            Parcels = parcels,
            ItemQuantities = itemQuantities
        };
    }

    private static string? Dec(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Result of matching planned quantities onto concrete shipment items.</summary>
public sealed record ItemMatchResult
{
    public IReadOnlyList<ItemQuantityMatch> Matches { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A resolved (shipmentItemId, quantity) pair ready for the PUT payload.</summary>
public sealed record ItemQuantityMatch
{
    public required string ShipmentItemId { get; init; }

    public required string Quantity { get; init; }
}
