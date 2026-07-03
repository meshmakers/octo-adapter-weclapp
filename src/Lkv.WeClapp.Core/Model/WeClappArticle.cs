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
    /// WeClapp Einkaufspreis (EK). DEFERRED: WeClapp has NO single top-level purchase-price field —
    /// the EK lives at the nested path Article.supplySources[] → ArticleSupplySource.articlePrices[].price
    /// (empty in the trial data). This property is a placeholder until that path is wired/confirmed
    /// against the real account, so it is usually null → DILOS EK-Preis = 0 (Jürgen 2026-06-29).
    /// </summary>
    public decimal? PurchasePrice { get; init; }
}
