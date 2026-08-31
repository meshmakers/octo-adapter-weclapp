using System.Globalization;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Mapping;

/// <summary>
/// WeClapp → DILOS value rules, verified against the golden files and Jürgen's feedback (2026-06-28/29).
/// </summary>
public static class WeClappToDilos
{
    /// <summary>DILOS currency key. Only EUR=5 is verified (12/12 real positions); unknown fails loud.</summary>
    public static int Currency(string recordCurrencyName) => recordCurrencyName switch
    {
        "EUR" => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(recordCurrencyName), recordCurrencyName,
            "Kein DILOS-Währungscode hinterlegt (bisher nur EUR=5 verifiziert).")
    };

    /// <summary>DILOS MwSt as integer percent (AI field 16), e.g. 20. AS tax key stays empty.</summary>
    public static int MwStPercent(decimal taxRate) => (int)Math.Round(taxRate, MidpointRounding.AwayFromZero);

    /// <summary>EK-Preis: WeClapp Einkaufspreis; null / not available → 0 (Jürgen 2026-06-29).</summary>
    public static decimal EkPreis(decimal? purchasePrice) => purchasePrice ?? 0m;

    public static bool IsSystemArticle(WeClappArticle a) => IsSystemArticle(a.ArticleType);

    /// <summary>The same rule on the bare article type, for the AS pipeline: it has to decide
    /// whether an article is loading equipment on the raw WeClapp payload, before the article is
    /// bound to the model.</summary>
    public static bool IsSystemArticle(string? articleType) => articleType == "LOADING_EQUIPMENT";

    public static bool IsSystemCustomer(string customerNumber) => customerNumber == "ANONYMOUS_DEBITOR";

    /// <summary>Fixed-decimal, InvariantCulture — DILOS uses dot, no thousands separator.</summary>
    public static string Dec(decimal value, int decimals) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>Money format = 2 decimals (prices, sums): e.g. 0 → "0.00", 29.99 → "29.99".</summary>
    public static string Money(decimal value) => Dec(value, 2);

    /// <summary>General number, trailing zeros trimmed, dot decimal: 0 → "0", 9.99 → "9.99", 5 → "5".</summary>
    public static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
