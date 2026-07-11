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

    /// <summary>
    /// In the raw GET /article response these are REFERENCE STUBS ({articleSupplySourceId},
    /// no prices — customer-verified 2026-07-08). The fetch side resolves them with the full
    /// <c>articleSupplySource</c> entities (which carry articlePrices) before parsing.
    /// </summary>
    public List<WeClappSupplySource> SupplySources { get; init; } = new();

    /// <summary>
    /// WeClapp Einkaufspreis (EK): first parseable supplySources[].articlePrices[].price of the
    /// ENRICHED shape. Null without a supply-source price → DILOS EK-Preis = 0 (Jürgen 2026-06-29).
    /// </summary>
    public decimal? PurchasePrice => SupplySources
        .SelectMany(s => s.ArticlePrices)
        .Select(p => decimal.TryParse(p.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : (decimal?)null)
        .FirstOrDefault(p => p is not null);
}

/// <summary>One entry of article.supplySources: a reference stub (raw) or a resolved
/// articleSupplySource entity (after fetch-side enrichment).</summary>
public sealed record WeClappSupplySource
{
    /// <summary>Stub reference to the separate articleSupplySource entity (raw shape only).</summary>
    public string ArticleSupplySourceId { get; init; } = "";

    public List<WeClappArticlePrice> ArticlePrices { get; init; } = new();
}

/// <summary>The separate GET /articleSupplySource entity (carries the purchase prices;
/// it has NO articleId — articles point at it via their supplySources stubs).</summary>
public sealed record WeClappArticleSupplySource
{
    public string Id { get; init; } = "";

    public List<WeClappArticlePrice> ArticlePrices { get; init; } = new();
}

public sealed record WeClappArticlePrice
{
    /// <summary>Amount as string, like every WeClapp money field.</summary>
    public string? Price { get; init; }
}
