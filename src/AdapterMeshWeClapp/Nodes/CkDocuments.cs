namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

// CK-shaped documents produced by WeClappToCkNode. Property names equal the
// Industry.Logistics CK attribute names (and the Basic Contact/Address record
// field names) so downstream built-in nodes (GetOrCreateRtEntitiesByType,
// CreateUpdateInfo/ApplyChanges) can address them 1:1 by path.

/// <summary>Basic "Address" record shape (fields per Octo.Sdk.Packages.Basic records/Address.yaml).</summary>
public sealed record CkAddress
{
    public string Street { get; init; } = "";
    public string Zipcode { get; init; } = "";
    public string CityTown { get; init; } = "";
    public string NationalCode { get; init; } = "";
}

/// <summary>Basic "Contact" record shape (fields per Octo.Sdk.Packages.Basic records/Contact.yaml).</summary>
public sealed record CkContact
{
    public string CompanyName { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public CkAddress? Address { get; init; }
}

/// <summary>Industry.Logistics "Customer" shape (NamedEntity Name + CustomerNumber + Contact record).</summary>
public sealed record CkCustomer
{
    /// <summary>WeClapp "customerNumber" — the business key (AI "ClientIdnummer").</summary>
    public string CustomerNumber { get; init; } = "";

    /// <summary>NamedEntity name: company if present, otherwise "FirstName LastName".</summary>
    public string Name { get; init; } = "";

    public CkContact Contact { get; init; } = new();
}

/// <summary>Industry.Logistics "Article" shape.</summary>
public sealed record CkArticle
{
    /// <summary>The stable WeClapp article id — the DILOS field contract key
    /// (AS field 3 and AI P* field 5 carry the same id via the writers).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>NamedEntity name (WeClapp article "name").</summary>
    public string Name { get; init; } = "";

    public string? Ean { get; init; }
}

/// <summary>Industry.Logistics "Order" shape.</summary>
public sealed record CkOrder
{
    /// <summary>WeClapp sales order id — equals AI "Auftragsnummer1" (writers use the same id).</summary>
    public string OrderNumber { get; init; } = "";

    /// <summary>WeClapp "orderNumber" (shop number) — equals AI "Auftragsnummer2".</summary>
    public string ExternalOrderNumber { get; init; } = "";

    public DateTime? OrderDate { get; init; }

    public DateTime? DeliveryDate { get; init; }
}

/// <summary>Industry.Logistics "OrderItem" shape (flattened from WeClapp orderItems).</summary>
public sealed record CkOrderItem
{
    public int PositionNumber { get; init; }

    public double Quantity { get; init; }

    /// <summary>Net unit price = netAmount / quantity (net position amount when quantity is 0).</summary>
    public double? UnitPriceNet { get; init; }

    /// <summary>WeClapp articleId of the position — association key to the Article entity.</summary>
    public string ArticleNumber { get; init; } = "";
}

/// <summary>Order-mode output document: customer + order + flattened items.</summary>
public sealed record CkOrderDocument
{
    public CkCustomer Customer { get; init; } = new();
    public CkOrder Order { get; init; } = new();
    public List<CkOrderItem> OrderItems { get; init; } = new();
}
