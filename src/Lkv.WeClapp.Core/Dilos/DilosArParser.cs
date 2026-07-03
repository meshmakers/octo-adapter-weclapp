namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Parses DILOS AR files ("Auftragsrückmeldung", orders dispatched) — the read side of the
/// LKV → WeClapp return path. Sequential grouping: a K* header opens a shipment, subsequent
/// C*/P*/L* records attach to it (OrderNumber1 guarded). Fail-loud via DilosParseException.
/// </summary>
public static class DilosArParser
{
    private const int HeaderFieldCount = 14;
    private const int ParcelFieldCount = 7;
    private const int ItemFieldCount = 12;
    private const int PackingFieldCount = 11;

    /// <summary>Parses AR file content (already decoded) into shipments.</summary>
    /// <exception cref="DilosParseException">On any structural defect (fail-loud).</exception>
    public static IReadOnlyList<DilosArShipment> Parse(string content)
    {
        var shipments = new List<DilosArShipment>();
        DilosArShipment? current = null;

        foreach (var (line, number) in DilosFormat.DataLines(content))
        {
            var f = line.Split('|');
            switch (f[0])
            {
                case "K*":
                    Require(f, HeaderFieldCount, number);
                    current = ReadHeader(f, number);
                    shipments.Add(current);
                    break;

                case "C*":
                    Require(f, ParcelFieldCount, number);
                    Attach(current, f[1], number, "C*").Parcels.Add(ReadParcel(f, number));
                    break;

                case "P*":
                    Require(f, ItemFieldCount, number);
                    Attach(current, f[1], number, "P*").Items.Add(ReadItem(f, number));
                    break;

                case "L*":
                    Require(f, PackingFieldCount, number);
                    Attach(current, f[1], number, "L*").PackingLines.Add(ReadPackingLine(f, number));
                    break;

                default:
                    throw new DilosParseException(number, $"Unknown AR record prefix '{f[0]}'");
            }
        }

        return shipments;
    }

    private static void Require(string[] f, int count, int lineNumber)
    {
        if (f.Length != count)
        {
            throw new DilosParseException(lineNumber, $"{f[0]} record has {f.Length} fields, expected {count}");
        }
    }

    private static DilosArShipment Attach(DilosArShipment? current, string orderNumber1, int lineNumber, string record)
    {
        if (current is null)
        {
            throw new DilosParseException(lineNumber, $"{record} record before first K* header");
        }

        if (orderNumber1 != current.OrderNumber1)
        {
            throw new DilosParseException(lineNumber,
                $"{record} record OrderNumber1 '{orderNumber1}' does not match current K* '{current.OrderNumber1}'");
        }

        return current;
    }

    private static DilosArShipment ReadHeader(string[] f, int n) => new()
    {
        Division = f[1],
        ClientId = f[2],
        InvoiceClientId = f[3],
        Zone = f[4],
        OrderNumber1 = f[5],
        OrderNumber2 = f[6],
        DilosOrderNumber = f[7],
        DilosForwardingNumber = f[8],
        Difference = f[9],
        ShipmentDate = DilosFormat.OptDate(f[10], n, "Datum"),
        TotalQuantity = DilosFormat.OptDec(f[11], n, "Gesamtmenge"),
        ParcelCount = DilosFormat.OptInt(f[12], n, "Summe Colli"),
        TotalWeight = DilosFormat.OptDec(f[13], n, "Summe Gewicht"),
    };

    private static DilosParcel ReadParcel(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        Carrier = f[2],
        TrackingNumber = f[3],
        PackagingType = f[4],
        ServiceType = f[5],
        Weight = DilosFormat.OptDec(f[6], n, "Gewicht"),
    };

    private static DilosArItem ReadItem(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        PositionNumber = DilosFormat.OptInt(f[2], n, "Position"),
        ArticleNumber = f[3],
        PartCondition = f[4],
        Characteristic1 = f[5],
        Characteristic2 = f[6],
        SerialNumber = f[7],
        BatchNumber = f[8],
        OrderedQuantity = DilosFormat.OptDec(f[9], n, "Auftragsmenge"),
        DeliveredQuantity = DilosFormat.Dec(f[10], n, "Menge geliefert"),
        OpenQuantity = DilosFormat.OptDec(f[11], n, "Offene Menge"),
    };

    private static DilosPackingLine ReadPackingLine(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        PositionNumber = DilosFormat.OptInt(f[2], n, "Position"),
        ArticleNumber = f[3],
        PartCondition = f[4],
        Characteristic1 = f[5],
        Characteristic2 = f[6],
        SerialNumber = f[7],
        BatchNumber = f[8],
        PackedQuantity = DilosFormat.Dec(f[9], n, "Packstückmenge"),
        TrackingNumber = f[10],
    };
}
