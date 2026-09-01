using System.Globalization;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Constants for an AI export run.</summary>
public sealed record DilosOrderContext
{
    /// <summary>WeClapp Mandanten-ID (constant per tenant) → DILOS Submandant; LKV maps it.</summary>
    public required string Submandant { get; init; }

    /// <summary>
    /// The RAW <c>taxValue</c> of every fetched WeClapp <c>tax</c> entity, by id. Raw on purpose:
    /// the rate is parsed and range-checked only for the entities a rendered position actually
    /// names, so one unusable entity somewhere in the account's tax list cannot fail the orders
    /// that are not taxed under it. Required rather than defaulted to empty, because an empty map
    /// renders every rate as an empty field - the LEGITIMATE value for a position that states no
    /// tax, and therefore invisible in the delivered file.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> TaxValueById { get; init; }
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
///
/// Because those prices ARE the contract now, an amount that is absent or unreadable fails the
/// order instead of rendering 0.00. That fallback predates the price agreement, and under it a
/// lost amount becomes an actively wrong statement to the partner which nothing downstream can
/// detect. A price of genuinely zero is legitimate and renders 0.00 exactly as before.
/// </summary>
public static class DilosOrderWriter
{
    private const int HeaderFieldCount = 66;
    private const int PositionFieldCount = 22;

    /// <summary>
    /// The only numeric shape a DILOS amount or rate may arrive in: dot decimal, optional sign,
    /// nothing else. Deliberately NOT <see cref="NumberStyles.Any" />, which also accepts a group
    /// separator and parentheses: under InvariantCulture "44,67" would then read as 4467 and
    /// "(20)" as -20 - silently, and straight into a delivered file.
    /// </summary>
    private const NumberStyles AmountStyles =
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;

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
        f[65] = WeClappToDilos.Money(          // RechnungssummeBrutto
            Amount(o.GrossAmount, "Rechnungssumme brutto", $"Order '{o.Id}'"));
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
            yield return RenderPosition(ctx, o.Id, pos, item.ArticleId, item.Quantity, item.Title,
                item.TaxId, item.NetAmount, item.GrossAmount);
        }

        // Versandkosten: eigene P*-Zeile mit Artikelnummer = -1 (← WeClapp shippingCostItems).
        // WeClapp states net, gross and taxId on these items exactly as on an article position and
        // the line prints on the same documents, so it renders through the SAME builder - quantity
        // 1 by construction, which makes its unit and line prices the same number.
        foreach (var ship in o.ShippingCostItems)
        {
            pos++;
            yield return RenderPosition(ctx, o.Id, pos, "-1", "1", ship.Title,
                ship.TaxId, ship.NetAmount, ship.GrossAmount);
        }
    }

    /// <summary>
    /// The ONE place a DILOS P* record is written. An article line and the shipping pseudo line
    /// differ only in what they hand in - article id and quantity, versus "-1" and 1 - so a future
    /// position field is written here once instead of in two places that can drift apart.
    /// </summary>
    private static string RenderPosition(DilosOrderContext ctx, string orderId, int pos,
        string articleNumber, string quantity, string title, string? taxId,
        string? netAmount, string? grossAmount)
    {
        var where = $"Order '{orderId}' position {pos}";
        var net = Amount(netAmount, "Positionspreis netto", where);
        var gross = Amount(grossAmount, "Positionspreis brutto", where);

        // The quantity is read the same loud way as the amounts, and for the same reason: fields 18
        // and 20 are DIVIDED by it, so it is an input to two contract values rather than the piece
        // of pass-through text field 11 makes it look like. A value that does not read as a number
        // used to answer 0, which PerUnit then treats as "nothing to divide by" - the record would
        // state the LINE amount as the unit price, too high by exactly the quantity (threefold on a
        // quantity of 3) while the line prices beside it stayed correct. Nothing downstream can see
        // that, and the model's own "" default makes the path reachable.
        var qty = Amount(quantity, "Mengeabg", where);

        var f = NewFields(PositionFieldCount);
        f[1] = "P*";
        f[2] = orderId;                                    // Auftragsnummer1
        // DILOS requires Position to be unique per Auftragsnummer. WeClapp's
        // positionNumber can have gaps and would collide with the appended
        // shipping pseudo line, so both position fields use the sequential
        // render index; return-path matching uses Artikelnummer, not Position.
        f[3] = pos.ToString(CultureInfo.InvariantCulture); // Position
        f[4] = pos.ToString(CultureInfo.InvariantCulture); // PositionnummerAufLieferschein
        f[5] = articleNumber;                              // Artikelnummer (= AS-Key)
        f[11] = quantity;                                  // Mengeabg
        f[14] = title;                                     // Text
        f[15] = "5";                                       // Währungsschlüssel = EUR
        f[16] = MwSt(taxId, ctx, where);                   // MwSt (Ganzzahl-Prozent)
        f[17] = "L";                                       // KennzeichenDruck = Lieferschein
        f[18] = WeClappToDilos.Money(PerUnit(net, qty));   // Einzelpreis netto
        f[19] = WeClappToDilos.Money(net);                 // Positionspreis netto
        f[20] = WeClappToDilos.Money(PerUnit(gross, qty)); // Einzelpreis brutto
        f[21] = WeClappToDilos.Money(gross);               // Positionspreis brutto
        return Join(f, PositionFieldCount);
    }

    /// <summary>
    /// DILOS P* field 16: the position's VAT rate in whole percent. A position that names no tax
    /// entity states no rate and leaves the field empty (spec: not mandatory; the partner's own
    /// files carry it empty throughout). A rate of ZERO is a stated rate and renders "0" - the
    /// customer's tax-free intra-EU supplies are taxed that way, and folding them into the empty
    /// field would make them indistinguishable from a position with no tax reference at all.
    ///
    /// The rate is parsed HERE and not where the tax set is indexed, so only the entities a
    /// rendered position actually names are ever validated: an unusable entity elsewhere in the
    /// account's tax list is none of this order's business.
    /// </summary>
    /// <exception cref="InvalidOperationException">The position names a tax entity that was not
    /// fetched, or one whose rate is unreadable or outside 0-100. Rendering an empty field instead
    /// would be indistinguishable from the legitimate "no tax stated" above, in a file that is
    /// delivered once and then marked as exported.</exception>
    private static string MwSt(string? taxId, DilosOrderContext ctx, string where)
    {
        if (string.IsNullOrEmpty(taxId))
        {
            return "";
        }

        if (!ctx.TaxValueById.TryGetValue(taxId, out var taxValue))
        {
            throw new InvalidOperationException(
                $"{where} is taxed under WeClapp tax '{taxId}', which is not among the " +
                $"{ctx.TaxValueById.Count} fetched tax entities - its MwSt would silently render as " +
                "an empty field, the value of a position that states no tax at all");
        }

        if (!decimal.TryParse(taxValue, AmountStyles, CultureInfo.InvariantCulture, out var rate))
        {
            throw new InvalidOperationException(
                $"{where} is taxed under WeClapp tax '{taxId}', whose rate '{taxValue ?? "<none>"}' " +
                "is not a plain decimal percentage");
        }

        // Outside 0-100 it is not a rate this field can state. DILOS field 16 is a whole-percent
        // integer, so a value that came from the wrong property - an amount, a tax key, a negative
        // correction - would otherwise be rounded and delivered as though it were one.
        if (rate is < 0m or > 100m)
        {
            throw new InvalidOperationException(
                $"{where} is taxed under WeClapp tax '{taxId}', whose rate {rate} is outside the " +
                "0 to 100 percent a MwSt field can state");
        }

        return WeClappToDilos.MwStPercent(rate).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A number the delivered record is built from - a price, or the quantity the unit prices are
    /// divided by. Absent, empty or unreadable fails the order: every one of those used to answer
    /// 0, and a 0 stand-in is an actively wrong statement which neither the file nor any downstream
    /// step can tell from a genuine zero, in a delivery that writes its export marker on the way
    /// out. A real zero parses and keeps its meaning.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is missing, empty, or not a plain
    /// decimal.</exception>
    private static decimal Amount(string? raw, string what, string where)
    {
        if (string.IsNullOrEmpty(raw))
        {
            throw new InvalidOperationException(
                $"{where} states no {what} - it is part of the delivered record, so a missing value " +
                "fails the order instead of being stood in for by 0");
        }

        if (!decimal.TryParse(raw, AmountStyles, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"{where} states the {what} as '{raw}', which is not a plain decimal number");
        }

        return value;
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

    /// <summary>Unit price from a LINE total: WeClapp states netAmount/grossAmount per position and
    /// its own unitPrice is the pre-discount list price, which matches neither. A quantity of zero
    /// keeps the line total rather than dividing by it - that is the one quantity this may happen
    /// for, because an unreadable one no longer reaches here.</summary>
    private static decimal PerUnit(decimal lineAmount, decimal quantity) =>
        quantity == 0m ? lineAmount : lineAmount / quantity;
}
