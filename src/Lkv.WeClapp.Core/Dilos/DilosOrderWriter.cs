using System.Globalization;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Constants for an AI export run.</summary>
public sealed record DilosOrderContext
{
    /// <summary>WeClapp Mandanten-ID (constant per tenant) → DILOS Submandant; LKV maps it.</summary>
    public required string Submandant { get; init; }
}

/// <summary>
/// Renders a WeClapp sales order into DILOS AI records (K* header + P* positions + the -1 shipping line),
/// field order per _specs/AI.md. Decided fields (Jürgen): ClientIdnummer=customerNumber (Warenempfänger),
/// Submandant=Mandanten-ID, Währung=5, Druck=L, kein Rechnungsdruck (Text4=0), Warenwerte gefüllt,
/// Frächter (field 33) = shipmentMethod id (empty when the order carries none — golden files are empty too).
/// Deferred (runtime-gated): MwSt% (field 16, needs tax-rate resolution; empty in all golden files),
/// Einzelpreis brutto (field 20, needs MwSt rate).
/// </summary>
public static class DilosOrderWriter
{
    private const int HeaderFieldCount = 66;
    private const int PositionFieldCount = 22;

    public static string RenderHeader(WeClappSalesOrder o, DilosOrderContext ctx)
    {
        var f = NewFields(HeaderFieldCount);
        f[1] = "K*";
        f[2] = o.CustomerNumber;               // ClientIdnummer = Kundennummer des Warenempfängers
        f[4] = ctx.Submandant;                 // Submandant = WeClapp Mandanten-ID (konstant)
        f[5] = o.DeliveryAddress.Company;      // Empfaengername1
        f[6] = PersonName(o.DeliveryAddress);  // Empfaengername2 = "Nachname Vorname" (Billbee-Parität;
                                               // B2C orders carry no company — 2026-07-16 finding)
        f[8] = o.DeliveryAddress.CountryCode;  // ELKZ
        f[9] = o.DeliveryAddress.Zipcode;      // EPLZ
        f[10] = o.DeliveryAddress.Street1;     // Estrasse_postfach
        f[11] = o.DeliveryAddress.City;        // Eort
        f[12] = o.DeliveryAddress.PhoneNumber; // Avisatelefon (carrier avis)
        f[15] = o.InvoiceAddress.Company;      // Rechnungsname1
        f[16] = PersonName(o.InvoiceAddress);  // Rechnungsname2
        f[18] = o.InvoiceAddress.CountryCode;  // RLKZ
        f[19] = o.InvoiceAddress.Zipcode;      // RPLZ
        f[20] = o.InvoiceAddress.Street1;      // Rstrasse_postfach
        f[21] = o.InvoiceAddress.City;         // Rort
        f[26] = Date(o.OrderDate);             // Auftragsdatum
        f[27] = o.PlannedShippingDate is { } d ? Date(d) : ""; // Lieferdatum
        f[30] = o.Id;                          // Auftragsnummer1 (= Lieferscheinnr / Rechnungs-PDF-Name)
        f[31] = o.OrderNumber;                 // Auftragsnummer2
        f[33] = o.ShipmentMethodId;            // Frächter = WeClapp shipmentMethod-ID (LKV mappt)
        f[46] = "0";                           // Text4: kein Rechnungsdruck
        f[65] = Money(o.GrossAmount);          // RechnungssummeBrutto
        return Join(f, HeaderFieldCount);
    }

    /// <summary>"Nachname Vorname" like the previous production connector wrote it —
    /// empty when the address carries no person (pure company addresses).</summary>
    private static string PersonName(WeClappAddress address) =>
        $"{address.LastName} {address.FirstName}".Trim();

    public static IEnumerable<string> RenderPositions(WeClappSalesOrder o, DilosOrderContext ctx)
    {
        var pos = 0;

        foreach (var item in o.OrderItems)
        {
            pos++;
            var f = NewFields(PositionFieldCount);
            f[1] = "P*";
            f[2] = o.Id;                                    // Auftragsnummer1
            // DILOS requires Position to be unique per Auftragsnummer. WeClapp's
            // positionNumber can have gaps and would collide with the appended
            // shipping pseudo line, so both position fields use the sequential
            // render index; return-path matching uses Artikelnummer, not Position.
            f[3] = pos.ToString(CultureInfo.InvariantCulture);                 // Position
            f[4] = pos.ToString(CultureInfo.InvariantCulture);                 // PositionnummerAufLieferschein
            f[5] = item.ArticleId;                          // Artikelnummer (= AS-Key)
            f[11] = item.Quantity;                          // Mengeabg
            f[14] = item.Title;                             // Text
            f[15] = "5";                                    // Währungsschlüssel = EUR
            f[17] = "L";                                    // KennzeichenDruck = Lieferschein
            f[18] = UnitNet(item.NetAmount, item.Quantity); // Einzelpreis netto
            yield return Join(f, PositionFieldCount);
        }

        // Versandkosten: eigene P*-Zeile mit Artikelnummer = -1 (← WeClapp shippingCostItems)
        foreach (var ship in o.ShippingCostItems)
        {
            pos++;
            var f = NewFields(PositionFieldCount);
            f[1] = "P*";
            f[2] = o.Id;
            f[3] = pos.ToString(CultureInfo.InvariantCulture);
            f[4] = pos.ToString(CultureInfo.InvariantCulture);
            f[5] = "-1";
            f[11] = "1";
            f[14] = ship.Title;
            f[15] = "5";
            f[17] = "L";
            f[18] = Money(ship.NetAmount);
            yield return Join(f, PositionFieldCount);
        }
    }

    private static string[] NewFields(int count)
    {
        var f = new string[count + 1];
        for (var i = 1; i <= count; i++) f[i] = "";
        return f;
    }

    private static string Join(string[] f, int count) => string.Join("|", f.Skip(1).Take(count));

    private static string Date(long epochMs) =>
        ViennaTime.ToViennaDate(epochMs).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Money(string? amount) => WeClappToDilos.Money(ParseDec(amount));

    private static decimal ParseDec(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string UnitNet(string? netAmount, string quantity)
    {
        var net = ParseDec(netAmount);
        var qty = ParseDec(quantity);
        return WeClappToDilos.Money(qty == 0m ? net : net / qty);
    }
}
