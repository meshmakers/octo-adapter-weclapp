using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Constants/context for an AS export run (kept minimal; extend as needed).</summary>
public sealed record DilosArticleContext
{
    public static readonly DilosArticleContext Default = new();
}

/// <summary>
/// Renders a WeClapp article into a DILOS AS <c>A*</c> record (pipe-delimited, field order per _specs/AS.md).
/// Decided fields (Jürgen): Lagereinheit=unitName, EK-Preis=Einkaufspreis/0, MwSt-Schlüssel + Währung leer.
/// </summary>
public static class DilosArticleWriter
{
    private const int FieldCount = 34;

    public static string RenderLine(WeClappArticle a, DilosArticleContext ctx)
    {
        var f = new string[FieldCount + 1]; // 1..34 used (DILOS 1-indexed); index 0 unused
        for (var i = 1; i <= FieldCount; i++) f[i] = "";

        f[1] = "A*";
        f[3] = a.Id;                 // Artikelnummer = stable WeClapp id (= AI position key)
        f[4] = a.Name;               // Bezeichnung1
        f[5] = a.ArticleNumber;      // Bezeichnung2 = SKU (WeClapp-native, not the old Merkmal2 hack)
        f[11] = a.Ean ?? "";         // EAN-Code
        f[12] = a.UnitName;          // Lagereinheit
        f[20] = WeClappToDilos.Num(WeClappToDilos.EkPreis(a.PurchasePrice)); // EK-Preis, 0 if missing
        f[23] = Artikelart(a);       // Artikelart
        // f[24] MwSt-Schlüssel and f[25] Währung stay empty (decided: tax as % at order, currency at position)

        return string.Join("|", f.Skip(1).Take(FieldCount));
    }

    // STORABLE → 1 (Einzel). Serial/charge/MHD flags are not in the minimal DTO yet → default 1 (matches golden).
    private static string Artikelart(WeClappArticle a) => "1";
}
