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

        // Replay protection: an already-SHIPPED shipment carrying the same tracking
        // number means this AR file was processed before (e.g. remote delete failed).
        var replay = existingShipments.FirstOrDefault(s =>
            s.Status == "SHIPPED" &&
            update.PackageTrackingNumber is not null &&
            s.PackageTrackingNumber == update.PackageTrackingNumber);
        if (replay is not null)
        {
            return new ArWritePlan
            {
                Action = ArWriteAction.Skip,
                SkipReason = $"Shipment {replay.Id} is already SHIPPED with tracking number {update.PackageTrackingNumber}",
                Warnings = warnings
            };
        }

        var reusable = existingShipments.FirstOrDefault(s => s.Status != "CANCELLED");
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

    /// <summary>
    /// Matches planned delivered quantities onto concrete WeClapp shipment items by article id.
    /// Unmatched entries on either side are reported as warnings, never guessed.
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

            matches.Add(new ItemQuantityMatch { ShipmentItemId = target.Id, Quantity = wanted.Quantity });
            matchedShipmentItemIds.Add(target.Id);
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
        string? shipmentTrackingNumber = null;
        string? shipmentTrackingUrl = null;
        string? carrier = null;

        for (var i = 0; i < ar.Parcels.Count; i++)
        {
            var p = ar.Parcels[i];
            var tracking = TrackingSplitter.Split(p.TrackingNumber);
            if (tracking.Numbers.Count > 1)
            {
                warnings.Add($"Parcel {i + 1}: {tracking.Numbers.Count} distinct tracking numbers in '{p.TrackingNumber}' — keeping all, using first");
            }

            var primary = tracking.Numbers.Count > 0 ? tracking.Numbers[0] : null;
            parcels.Add(new WeClappParcelWrite
            {
                PositionNumber = i + 1,
                TrackingId = primary,
                TrackingUrl = tracking.Url,
                Weight = Dec(p.Weight)
            });

            if (i == 0)
            {
                shipmentTrackingNumber = primary;
                shipmentTrackingUrl = tracking.Url;
                if (DilosCarrierMap.TryMap(p.Carrier, out var mapped))
                {
                    carrier = mapped;
                }
                else
                {
                    warnings.Add($"Unknown DILOS carrier code '{p.Carrier}' — writing tracking without carrier reference");
                }
            }
        }

        if (ar.Difference == "2")
        {
            warnings.Add("DILOS Differenzen=2: shortages exist and are not backordered by DILOS — writing delivered quantities only");
        }

        var itemQuantities = new List<ArItemQuantity>();
        foreach (var item in ar.Items)
        {
            if (item.ArticleNumber.Length == 0)
            {
                continue; // shipping-cost pseudo item — never a WeClapp shipment item
            }

            if (item.OpenQuantity is < 0)
            {
                warnings.Add($"Article {item.ArticleNumber}: over-delivery (open quantity {item.OpenQuantity}) — passing delivered quantity through");
            }

            itemQuantities.Add(new ArItemQuantity
            {
                ArticleId = item.ArticleNumber,
                Quantity = Dec(item.DeliveredQuantity)!
            });
        }

        return new ArShipmentUpdate
        {
            PackageTrackingNumber = shipmentTrackingNumber,
            PackageTrackingUrl = shipmentTrackingUrl,
            ShippingDateEpochMs = ar.ShipmentDate is { } d
                ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds()
                : null,
            TotalWeight = Dec(ar.TotalWeight),
            EcommerceShippingCarrier = carrier,
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
