using System.Globalization;

namespace Lkv.WeClapp.Core.Model;

/// <summary>WeClapp article (subset relevant for the DILOS AS export).</summary>
public sealed record WeClappArticle
{
    public string Id { get; init; } = "";
    public string ArticleNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string UnitName { get; init; } = "";
    public string ArticleType { get; init; } = "";
    public string? Ean { get; init; }

    /// <summary>Embedded in the default GET /article response (confirmed at the customer
    /// account 2026-07-07: 16 supply sources, 10 with articlePrices, field <c>price</c>).</summary>
    public List<WeClappSupplySource> SupplySources { get; init; } = new();

    /// <summary>
    /// WeClapp Einkaufspreis (EK): first parseable supplySources[].articlePrices[].price.
    /// Null when the article has no supply-source price → DILOS EK-Preis = 0 (Jürgen 2026-06-29).
    /// </summary>
    public decimal? PurchasePrice => SupplySources
        .SelectMany(s => s.ArticlePrices)
        .Select(p => decimal.TryParse(p.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : (decimal?)null)
        .FirstOrDefault(p => p is not null);
}

public sealed record WeClappSupplySource
{
    public List<WeClappArticlePrice> ArticlePrices { get; init; } = new();
}

public sealed record WeClappArticlePrice
{
    /// <summary>Amount as string, like every WeClapp money field.</summary>
    public string? Price { get; init; }
}
