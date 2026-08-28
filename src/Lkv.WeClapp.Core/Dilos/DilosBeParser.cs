namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Stock condition (BE field 6 "Zustand"): VER → Available, GES → Blocked.
/// Names align with the Industry.Logistics CK enum StockStatus.</summary>
public enum DilosStockStatus
{
    /// <summary>DILOS "VER" (verfügbar / available).</summary>
    Available,

    /// <summary>DILOS "GES" (gesperrt / not available).</summary>
    Blocked,
}

/// <summary>One line of a DILOS BE file ("Bestandsmeldung", stock report). 6 pipe fields, no record prefix.</summary>
public sealed record DilosStockLine
{
    /// <summary>DILOS "Artikelnummer", field 1 in both layouts. The key the write side matches on.</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>
    /// Article code / SKU: field 2 of the seven-field layout, absent from the six-field one where
    /// it stays empty. Carried for diagnostics - the article is identified by
    /// <see cref="ArticleNumber" />, never by this.
    /// </summary>
    public string ArticleCode { get; init; } = "";

    /// <summary>DILOS "Merkmal 1", e.g. colour; raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal 2", e.g. size; raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Lotnummer"; empty in the specification and in every file seen so far.</summary>
    public string LotNumber { get; init; } = "";

    /// <summary>DILOS "Menge": stock quantity including open order quantities in DILOS.</summary>
    public decimal Quantity { get; init; }

    /// <summary>DILOS "Zustand": VER/GES.</summary>
    public DilosStockStatus Status { get; init; }
}

/// <summary>
/// Parses DILOS BE files ("Bestandsmeldung", stock report) — the read side of the
/// LKV → WeClapp return path. Fail-loud on structural defects (field count, unknown
/// status, unparsable quantity); see the design spec for the golden-file evidence.
/// </summary>
public static class DilosBeParser
{
    private const int SpecFieldCount = 6;
    private const int WithArticleCodeFieldCount = 7;

    /// <summary>Parses BE file content (already decoded) into stock lines.</summary>
    /// <exception cref="DilosParseException">On any structural defect (fail-loud).</exception>
    public static IReadOnlyList<DilosStockLine> Parse(string content)
    {
        var result = new List<DilosStockLine>();

        foreach (var (line, number) in DilosFormat.DataLines(content))
        {
            var f = line.Split('|');

            // Two layouts are in the field. The specification and 1114 golden lines have six
            // fields; the customer's instance sends seven, the extra one being the article code in
            // second position, which shifts everything after it. The count tells them apart with no
            // guessing, and any other count is still a structural defect that has to be loud rather
            // than parsed shifted into WeClapp.
            var hasArticleCode = f.Length == WithArticleCodeFieldCount;
            if (f.Length != SpecFieldCount && !hasArticleCode)
            {
                throw new DilosParseException(number,
                    $"BE record has {f.Length} fields, expected {SpecFieldCount} or {WithArticleCodeFieldCount}");
            }

            var offset = hasArticleCode ? 1 : 0;
            var status = f[5 + offset];

            result.Add(new DilosStockLine
            {
                ArticleNumber = f[0],
                ArticleCode = hasArticleCode ? f[1] : "",
                Characteristic1 = f[1 + offset],
                Characteristic2 = f[2 + offset],
                LotNumber = f[3 + offset],
                Quantity = DilosFormat.Dec(f[4 + offset], number, "Menge"),
                Status = status switch
                {
                    "VER" => DilosStockStatus.Available,
                    "GES" => DilosStockStatus.Blocked,
                    _ => throw new DilosParseException(number, $"Unknown Zustand '{status}' (expected VER or GES)"),
                },
            });
        }

        return result;
    }
}
