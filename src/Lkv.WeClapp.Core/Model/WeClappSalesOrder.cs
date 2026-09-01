namespace Lkv.WeClapp.Core.Model;

/// <summary>WeClapp sales order (subset relevant for the DILOS AI export). Amounts/quantities are
/// strings in the WeClapp API and are parsed in the mapping/render layer.</summary>
public sealed record WeClappSalesOrder
{
    public string Id { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public string CustomerId { get; init; } = "";
    public string CustomerNumber { get; init; } = "";
    public string RecordCurrencyName { get; init; } = "";
    public string? NetAmount { get; init; }
    public string? GrossAmount { get; init; }
    public long OrderDate { get; init; }                 // epoch ms
    public long? PlannedShippingDate { get; init; }      // epoch ms
    /// <summary>WeClapp shipmentMethod id → DILOS K* Frächter (Jürgen wants the id, 2026-06-28).
    /// Customer reality 2026-07-08: 0/87 orders carry it — the field stays empty then, which the
    /// golden files prove importable ("Auswahl erfolgt bei LKV").</summary>
    public string ShipmentMethodId { get; init; } = "";
    public WeClappAddress DeliveryAddress { get; init; } = new();
    public WeClappAddress InvoiceAddress { get; init; } = new();
    public List<WeClappOrderItem> OrderItems { get; init; } = new();
    public List<WeClappShippingCostItem> ShippingCostItems { get; init; } = new();
}

public sealed record WeClappOrderItem
{
    public int PositionNumber { get; init; }
    public string ArticleId { get; init; } = "";
    public string ArticleNumber { get; init; } = "";
    public string Quantity { get; init; } = "";

    /// <summary>Line total net — NOT a unit price. WeClapp's own <c>unitPrice</c> is the
    /// pre-discount list price and matches neither total, so DILOS P* field 19 carries this value
    /// and field 18 divides it by the quantity.</summary>
    public string? NetAmount { get; init; }

    /// <summary>Line total gross, stated by WeClapp itself rather than derived from the net and a
    /// rate. DILOS P* field 21 carries it, field 20 divides it by the quantity.</summary>
    public string? GrossAmount { get; init; }

    public string Title { get; init; } = "";

    /// <summary>The WeClapp <c>tax</c> entity this position is taxed under, and the only route to
    /// its rate: the position states no percentage of its own, so DILOS P* field 16 is resolved
    /// against the separately fetched tax set (live customer account: 95/95 positions carry it).
    /// The payload's <c>taxName</c> and <c>unitName</c> are deliberately not modelled - a label and
    /// a unit of measure, neither of which the AI position record states.</summary>
    public string? TaxId { get; init; }
}

public sealed record WeClappShippingCostItem
{
    public string? NetAmount { get; init; }

    /// <summary>Line total gross. Shipping cost items carry the same amounts and tax reference as
    /// article positions, and the DILOS shipping pseudo line states the same price fields.
    /// </summary>
    public string? GrossAmount { get; init; }

    public string? TaxId { get; init; }
    public string Title { get; init; } = "";
}

public sealed record WeClappAddress
{
    public string Company { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Street1 { get; init; } = "";
    public string Zipcode { get; init; } = "";
    public string City { get; init; } = "";
    public string CountryCode { get; init; } = "";

    /// <summary>API field `phoneNumber` — present on real customer shop orders
    /// (live-verified 2026-07-16); feeds the DILOS Avisatelefon field.</summary>
    public string PhoneNumber { get; init; } = "";
}
