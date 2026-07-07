namespace Lkv.WeClapp.Core.WriteBack;

/// <summary>
/// Slim view of an existing WeClapp shipment (GET /shipment?salesOrderId-eq=...),
/// used for idempotency decisions before writing AR results back.
/// </summary>
public sealed record WeClappShipmentSummary
{
    public string Id { get; init; } = "";

    public string? SalesOrderId { get; init; }

    /// <summary>WeClapp shipment status: NEW | DELIVERY_NOTE_PRINTED | SHIPPED | IN_ROUTE | DELIVERED | CANCELLED.</summary>
    public string Status { get; init; } = "";

    public string? PackageTrackingNumber { get; init; }

    public List<WeClappShipmentItem> ShipmentItems { get; init; } = new();
}

/// <summary>Slim view of a WeClapp shipmentItem (write-back matches these by ArticleId).</summary>
public sealed record WeClappShipmentItem
{
    public string? Id { get; init; }

    public string? ArticleId { get; init; }

    public string? SalesOrderItemId { get; init; }

    public string? Quantity { get; init; }
}

/// <summary>One parcel to write onto a WeClapp shipment (parcels[i]; positionNumber is required by the API).</summary>
public sealed record WeClappParcelWrite
{
    public required int PositionNumber { get; init; }

    public string? TrackingId { get; init; }

    public string? TrackingUrl { get; init; }

    /// <summary>Weight as dot-decimal string (WeClapp decimals are strings).</summary>
    public string? Weight { get; init; }
}
