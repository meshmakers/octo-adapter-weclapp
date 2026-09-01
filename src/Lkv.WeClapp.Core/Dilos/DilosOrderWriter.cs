using System.Globalization;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Constants for an AI export run.</summary>
public sealed record DilosOrderContext
{
    /// <summary>WeClapp Mandanten-ID (constant per tenant) → DILOS Submandant; LKV maps it.</summary>
    public required string Submandant { get; init; }

    /// <summary>VAT rate in whole percent per WeClapp <c>tax</c> id — the rates the positions
    /// state in P* field 16. Required rather than defaulted to empty: an empty map renders every
    /// rate as an empty field, which is the LEGITIMATE value for a position that states no tax,
    /// so a forgotten map would produce files nothing downstream can tell from correct ones.
    /// </summary>
    public required IReadOnlyDictionary<string, int> TaxRatePercentById { get; init; }
}

/// <summary>
/// Renders a WeClapp sales order into DILOS AI records (K* header + P* positions + the -1 shipping line),
/// field order per _specs/AI.md. Decided fields (Jürgen): ClientIdnummer=customerNumber (Warenempfänger),
/// Submandant=Mandanten-ID, Währung=5, Druck=L, kein Rechnungsdruck (Text4=0), Warenwerte gefüllt,
/// Frächter (field 33) = shipmentMethod id (empty when the order carries none — golden files are empty too).
///
/// A position states its VAT rate and all four price fields (16, 18-21). The partner's own files are
/// NOT the reference for which of them carry a value: they fill 18 and 20 and leave 16, 19 and 21
/// empty, which is what the previous shop connector happened to produce. The contract is that each
/// position states its rate in whole percent and both its unit and its line price, net and gross.
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
        f[5] = RecipientName1(o.DeliveryAddress);  // Empfaengername1 (Pflicht): Firma, sonst Person
        f[6] = RecipientName2(o.DeliveryAddress);  // Empfaengername2: Person NEBEN einer Firma, sonst leer
        f[8] = o.DeliveryAddress.CountryCode;  // ELKZ
        f[9] = o.DeliveryAddress.Zipcode;      // EPLZ
        f[10] = o.DeliveryAddress.Street1;     // Estrasse_postfach
        f[11] = o.DeliveryAddress.City;        // Eort
        f[12] = o.DeliveryAddress.PhoneNumber; // Avisatelefon (carrier avis)
        // f[14] Avisa-email stays empty for now: the previous connector sourced it from an
        // address-level email, which WeClapp addresses do not carry — which email (if any)
        // belongs on the carrier avis is a pending decision with the logistics partner.
        f[15] = RecipientName1(o.InvoiceAddress);  // Rechnungsname1 (Pflicht): Firma, sonst Person
        f[16] = RecipientName2(o.InvoiceAddress);  // Rechnungsname2: Person neben der Firma
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

    /// <summary>Mandatory name1 (spec "L+R"): the company, or the person for B2C —
    /// "FirstName LastName", the shape every DILOS-import-proven golden file carries.</summary>
    private static string RecipientName1(WeClappAddress address) =>
        address.Company.Length > 0
            ? address.Company
            : $"{address.FirstName} {address.LastName}".Trim();

    /// <summary>Optional name2: the person NEXT TO a company ("Nachname Vorname", the
    /// previous connector's column mapping) — empty when the person already fills name1
    /// (golden B2C files keep name2 empty).</summary>
    private static string RecipientName2(WeClappAddress address) =>
        address.Company.Length > 0
            ? $"{address.LastName} {address.FirstName}".Trim()
            : "";

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
            f[16] = MwSt(item.TaxId, ctx, o.Id, pos);       // MwSt (Ganzzahl-Prozent)
            f[17] = "L";                                    // KennzeichenDruck = Lieferschein
            f[18] = UnitPrice(item.NetAmount, item.Quantity);  // Einzelpreis netto
            f[19] = Money(item.NetAmount);                     // Positionspreis netto
            f[20] = UnitPrice(item.GrossAmount, item.Quantity); // Einzelpreis brutto
            f[21] = Money(item.GrossAmount);                   // Positionspreis brutto
            yield return Join(f, PositionFieldCount);
        }

        // Versandkosten: eigene P*-Zeile mit Artikelnummer = -1 (← WeClapp shippingCostItems).
        // WeClapp states net, gross and taxId on these items exactly as on an article position,
        // and the line is printed on the same documents, so it carries the same price fields.
        // Quantity is 1 by construction, which makes unit and line price the same number.
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
            f[16] = MwSt(ship.TaxId, ctx, o.Id, pos);
            f[17] = "L";
            f[18] = Money(ship.NetAmount);
            f[19] = Money(ship.NetAmount);
            f[20] = Money(ship.GrossAmount);
            f[21] = Money(ship.GrossAmount);
            yield return Join(f, PositionFieldCount);
        }
    }

    /// <summary>
    /// DILOS P* field 16: the position's VAT rate in whole percent, resolved through the fetched
    /// WeClapp tax entities. A position that names no tax entity states no rate and leaves the
    /// field empty (spec: not mandatory; the partner's own files carry it empty throughout).
    /// </summary>
    /// <exception cref="InvalidOperationException">The position names a tax entity that is not in
    /// the fetched set. Falling back to an empty field instead would be indistinguishable from the
    /// legitimate "no tax stated" above, in a file that is delivered once and then marked as
    /// exported — so the order fails and the next tick retries it.</exception>
    private static string MwSt(string? taxId, DilosOrderContext ctx, string orderId, int position)
    {
        if (string.IsNullOrEmpty(taxId))
        {
            return "";
        }

        if (!ctx.TaxRatePercentById.TryGetValue(taxId, out var percent))
        {
            throw new InvalidOperationException(
                $"Order '{orderId}' position {position} is taxed under WeClapp tax '{taxId}', which is " +
                $"not among the {ctx.TaxRatePercentById.Count} fetched tax entities - its MwSt would " +
                "silently render as an empty field, the value of a position that states no tax at all");
        }

        return percent.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>Unit price from a LINE total: WeClapp states netAmount/grossAmount per position and
    /// its own unitPrice is the pre-discount list price, which matches neither. A zero quantity
    /// keeps the line total rather than dividing by it.</summary>
    private static string UnitPrice(string? lineAmount, string quantity)
    {
        var amount = ParseDec(lineAmount);
        var qty = ParseDec(quantity);
        return WeClappToDilos.Money(qty == 0m ? amount : amount / qty);
    }
}
