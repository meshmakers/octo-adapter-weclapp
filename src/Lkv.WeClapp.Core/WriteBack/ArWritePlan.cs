namespace Lkv.WeClapp.Core.WriteBack;

public enum ArWriteAction
{
    /// <summary>Nothing to write (reason in <see cref="ArWritePlan.SkipReason"/>).</summary>
    Skip,

    /// <summary>Update the existing shipment identified by <see cref="ArWritePlan.ExistingShipmentId"/>.</summary>
    UpdateExisting,

    /// <summary>No usable shipment exists: POST /salesOrder/id/{id}/createShipment first, then apply the update.</summary>
    CreateThenUpdate
}

/// <summary>
/// Pure write plan for one DILOS AR shipment: what to do against WeClapp and with which
/// field values. Produced by <see cref="ArShipmentWritePlanner"/> without any I/O.
/// </summary>
public sealed record ArWritePlan
{
    public required ArWriteAction Action { get; init; }

    public string? SkipReason { get; init; }

    public string? ExistingShipmentId { get; init; }

    public ArShipmentUpdate? Update { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Field values to apply to the WeClapp shipment (PUT /shipment/id/{id}).</summary>
public sealed record ArShipmentUpdate
{
    /// <summary>The shipment status to set as the LAST write step.</summary>
    public const string TargetStatus = "SHIPPED";

    public string? PackageTrackingNumber { get; init; }

    public string? PackageTrackingUrl { get; init; }

    /// <summary>Dispatch date (K* Datum) as epoch milliseconds UTC.</summary>
    public long? ShippingDateEpochMs { get; init; }

    /// <summary>Total weight in kg as dot-decimal string.</summary>
    public string? TotalWeight { get; init; }

    /// <summary>Raw C* field 3 of the first parcel. Primary carrier reference since 2026-07-08:
    /// LKV returns the carrier id as configured in the shop system, i.e. the WeClapp
    /// shippingCarrier entity id — the node resolves it against the live carrier list.</summary>
    public string? CarrierToken { get; init; }

    /// <summary>Legacy fallback: WeClapp ecommerceShippingCarrier constant mapped from a
    /// DILOS/Billbee-era code via <see cref="Dilos.DilosCarrierMap"/>, null when unmapped.</summary>
    public string? EcommerceShippingCarrier { get; init; }

    /// <summary>Transitional fallback for legacy codes whose carrier has NO WeClapp
    /// ecommerce constant (300 = GLS): the node matches this against the live carrier
    /// list by name, case-insensitive. Null when the code needs no name path.</summary>
    public string? CarrierName { get; init; }

    public IReadOnlyList<WeClappParcelWrite> Parcels { get; init; } = [];

    /// <summary>Delivered quantities per WeClapp article id (P* lines; shipping pseudo-item excluded).</summary>
    public IReadOnlyList<ArItemQuantity> ItemQuantities { get; init; } = [];
}

/// <summary>Delivered quantity for one article (matched to shipmentItems by ArticleId, never by position).</summary>
public sealed record ArItemQuantity
{
    public required string ArticleId { get; init; }

    /// <summary>Quantity as dot-decimal string.</summary>
    public required string Quantity { get; init; }
}
