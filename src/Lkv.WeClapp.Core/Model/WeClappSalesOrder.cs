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
    public string? NetAmount { get; init; }
    public string Title { get; init; } = "";
    public string UnitName { get; init; } = "";
    public string? TaxName { get; init; }
}

public sealed record WeClappShippingCostItem
{
    public string? NetAmount { get; init; }
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
}
