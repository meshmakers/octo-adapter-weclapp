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
    /// <summary>DILOS "Artikelnummer" (field 1).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Merkmal 1" (field 2), e.g. colour; raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal 2" (field 3), e.g. size; raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Lotnummer" (field 4); empty in spec and golden files.</summary>
    public string LotNumber { get; init; } = "";

    /// <summary>DILOS "Menge" (field 5): stock quantity including open order quantities in DILOS.</summary>
    public decimal Quantity { get; init; }

    /// <summary>DILOS "Zustand" (field 6): VER/GES.</summary>
    public DilosStockStatus Status { get; init; }
}

/// <summary>
/// Parses DILOS BE files ("Bestandsmeldung", stock report) — the read side of the
/// LKV → WeClapp return path. Fail-loud on structural defects (field count, unknown
/// status, unparsable quantity); see the design spec for the golden-file evidence.
/// </summary>
public static class DilosBeParser
{
    private const int FieldCount = 6;

    /// <summary>Parses BE file content (already decoded) into stock lines.</summary>
    /// <exception cref="DilosParseException">On any structural defect (fail-loud).</exception>
    public static IReadOnlyList<DilosStockLine> Parse(string content)
    {
        var result = new List<DilosStockLine>();

        foreach (var (line, number) in DilosFormat.DataLines(content))
        {
            var f = line.Split('|');
            if (f.Length != FieldCount)
            {
                throw new DilosParseException(number, $"BE record has {f.Length} fields, expected {FieldCount}");
            }

            result.Add(new DilosStockLine
            {
                ArticleNumber = f[0],
                Characteristic1 = f[1],
                Characteristic2 = f[2],
                LotNumber = f[3],
                Quantity = DilosFormat.Dec(f[4], number, "Menge"),
                Status = f[5] switch
                {
                    "VER" => DilosStockStatus.Available,
                    "GES" => DilosStockStatus.Blocked,
                    _ => throw new DilosParseException(number, $"Unknown Zustand '{f[5]}' (expected VER or GES)"),
                },
            });
        }

        return result;
    }
}
