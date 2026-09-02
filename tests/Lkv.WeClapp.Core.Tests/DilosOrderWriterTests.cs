using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

/// <summary>
/// The AI writer contract. NOTE on the golden files (Fixtures/AI5910986621265.txt and its
/// siblings): they are real LKV artefacts and are never edited - but they are a PROTOCOL OF THE
/// PREVIOUS SHOP CONNECTOR, not a statement of today's contract. They duplicate the same number
/// into fields 18 and 20 and leave 16, 19 and 21 empty; the agreement is that a position states
/// its rate in whole percent and both its unit and its line price, net and gross. The golden files
/// stay authoritative for LAYOUT (66/22 fields, dot decimals, LF) and for what the LKV import
/// demonstrably accepts. What the price fields must CONTAIN is pinned here and in
/// PipelineChainIntegrationTests, never by reading those files.
/// </summary>
public class DilosOrderWriterTests
{
    private static string Field(string line, int dilosFieldNo) => line.Split('|')[dilosFieldNo - 1];

    // The rate a position states comes from the WeClapp /tax entity its taxId points at; 3681 is
    // the id the committed salesOrder fixture references. The map carries the RAW taxValue, the way
    // the API states it - the writer parses only the entities a rendered position actually names.
    private static readonly DilosOrderContext Ctx = new()
    {
        Submandant = "51696697501",
        TaxValueById = new Dictionary<string, string?>
        {
            ["3681"] = "20",
            ["3682"] = "19",
            ["3683"] = "0",
        },
    };

    private static DilosOrderContext CtxWithTax(string taxId, string? taxValue) => new()
    {
        Submandant = "51696697501",
        TaxValueById = new Dictionary<string, string?> { [taxId] = taxValue },
    };

    [Fact]
    public void RenderHeader_MapsDecidedFieldsAndAddress()
    {
        const long orderDate = 1707177600000L; // 2024-02-06 UTC
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",
            CustomerNumber = "7067387625809",
            GrossAmount = "104.97",
            OrderDate = orderDate,
            ShipmentMethodId = "3415",
            DeliveryAddress = new WeClappAddress
            {
                Company = "TJ Lucas",
                CountryCode = "DE",
                Zipcode = "51503",
                Street1 = "Im Wielputzfeld 15a",
                City = "Rösrath"
            }
        };

        var k = DilosOrderWriter.RenderHeader(o, Ctx);

        Assert.Equal("K*", Field(k, 1));
        Assert.Equal("7067387625809", Field(k, 2));   // ClientIdnummer = customerNumber (Warenempfänger)
        Assert.Equal("51696697501", Field(k, 4));     // Submandant = Mandanten-ID (konstant)
        Assert.Equal("TJ Lucas", Field(k, 5));        // Empfaengername1
        Assert.Equal("DE", Field(k, 8));              // ELKZ
        Assert.Equal("51503", Field(k, 9));           // EPLZ
        Assert.Equal("Im Wielputzfeld 15a", Field(k, 10)); // Estrasse_postfach
        Assert.Equal("Rösrath", Field(k, 11));        // Eort
        Assert.Equal("06.02.2024", Field(k, 26));     // Auftragsdatum (Vienna calendar day)
        Assert.Equal("5910986621265", Field(k, 30));  // Auftragsnummer1
        Assert.Equal("74299", Field(k, 31));          // Auftragsnummer2
        Assert.Equal("3415", Field(k, 33));           // Frächter = WeClapp shipmentMethod-ID (Jürgen 2026-06-28)
        Assert.Equal("0", Field(k, 46));              // Text4: kein Rechnungsdruck
        Assert.Equal("104.97", Field(k, 65));         // RechnungssummeBrutto
        Assert.Equal(66, k.Split('|').Length);
    }

    [Fact]
    public void RenderHeader_B2cAddresses_WritePersonNamesAndAvisPhone()
    {
        // A B2C test import arrived at LKV without any recipient name (2026-07-16). The
        // DILOS-import-proven golden files pin the layout: the MANDATORY name1 fields
        // (f5/f15, spec "L+R") always carry a name — for B2C that is the person
        // ("FirstName LastName", golden order), name2 stays empty; only when a company
        // occupies name1 does name2 carry the person ("LastName FirstName", Billbee
        // column mapping). Avisatelefon comes from the delivery phone — real customer
        // shop orders carry firstName/lastName/phoneNumber (live-verified).
        var o = new WeClappSalesOrder
        {
            Id = "622075",
            OrderNumber = "SO-1001",
            CustomerNumber = "K-77",
            GrossAmount = "0.00",
            DeliveryAddress = new WeClappAddress
            {
                FirstName = "Erika",
                LastName = "Muster",
                PhoneNumber = "+43 660 1234567",
                CountryCode = "AT",
                Zipcode = "5400",
                Street1 = "Weg 1",
                City = "Hallein"
            },
            InvoiceAddress = new WeClappAddress { Company = "Muster GmbH", FirstName = "Max", LastName = "Muster" }
        };

        var k = DilosOrderWriter.RenderHeader(o, Ctx);

        Assert.Equal("Erika Muster", Field(k, 5));     // B2C: person fills the MANDATORY name1 (golden layout)
        Assert.Equal("", Field(k, 6));                 // name2 empty when the person already is name1
        Assert.Equal("+43 660 1234567", Field(k, 12)); // Avisatelefon
        Assert.Equal("Muster GmbH", Field(k, 15));     // Rechnungsname1 = company when present
        Assert.Equal("Muster Max", Field(k, 16));      // Rechnungsname2 = person next to the company
        Assert.Equal(66, k.Split('|').Length);
    }

    [Fact]
    public void RenderHeader_OrderDateAtViennaMidnight_KeepsTheAustrianCalendarDay()
    {
        // Real WeClapp date-picker values are account-local (Vienna) midnight:
        // 2024-02-05T23:00Z is 06.02.2024 00:00 CET — the AI date must be 06.02., not 05.02.
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",
            CustomerNumber = "7067387625809",
            GrossAmount = "104.97",
            OrderDate = 1707174000000L
        };

        var k = DilosOrderWriter.RenderHeader(o, Ctx);

        Assert.Equal("06.02.2024", Field(k, 26));
    }

    [Fact]
    public void RenderPositions_RendersItemThenShippingMinusOneLine()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", GrossAmount = "35.99",
                    Title = "Ersatzglas VOLT"
                }
            },
            ShippingCostItems =
            {
                new WeClappShippingCostItem
                {
                    NetAmount = "4.50", GrossAmount = "5.40", Title = "DHL Standard (DE)",
                },
            }
        };

        var lines = DilosOrderWriter.RenderPositions(o, Ctx).ToList();

        Assert.Equal(2, lines.Count); // 1 item + 1 shipping (-1) line

        var p = lines[0];
        Assert.Equal("P*", Field(p, 1));
        Assert.Equal("5910986621265", Field(p, 2));   // Auftragsnummer1
        Assert.Equal("1", Field(p, 3));               // Position
        Assert.Equal("43222003744925", Field(p, 5));  // Artikelnummer
        Assert.Equal("1", Field(p, 11));              // Mengeabg
        Assert.Equal("Ersatzglas VOLT", Field(p, 14));// Text
        Assert.Equal("5", Field(p, 15));              // Währungsschlüssel
        Assert.Equal("L", Field(p, 17));              // KennzeichenDruck
        Assert.Equal("29.99", Field(p, 18));          // Einzelpreis netto
        Assert.Equal(22, p.Split('|').Length);

        var ship = lines[1];
        Assert.Equal("-1", Field(ship, 5));           // Versandkosten-Zeile
        Assert.Equal("4.50", Field(ship, 18));        // Versandkosten netto
    }

    // The four price fields the partner is owed on top of the two that already shipped. WeClapp
    // states BOTH line totals (netAmount/grossAmount) and no unit price that matches them - its
    // own unitPrice is the pre-discount list price - so the totals are carried verbatim into the
    // Positionspreis fields and the two Einzelpreis fields are derived from them by the quantity,
    // which is the rule field 18 already followed.
    [Fact]
    public void RenderPositions_FillsEveryPriceFieldOfThePositionRecord()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925", Quantity = "2",
                    NetAmount = "59.98", GrossAmount = "71.98", TaxId = "3681",
                    Title = "Ersatzglas VOLT",
                },
            },
        };

        var p = DilosOrderWriter.RenderPositions(o, Ctx).Single();

        Assert.Equal("20", Field(p, 16));    // MwSt: integer percent, never a DILOS tax key
        Assert.Equal("29.99", Field(p, 18)); // Einzelpreis netto  = netAmount / quantity
        Assert.Equal("59.98", Field(p, 19)); // Positionspreis netto  = netAmount
        Assert.Equal("35.99", Field(p, 20)); // Einzelpreis brutto = grossAmount / quantity
        Assert.Equal("71.98", Field(p, 21)); // Positionspreis brutto = grossAmount
        Assert.Equal(22, p.Split('|').Length);
    }

    // The VAT field is the rate in whole percent, NOT the DILOS tax key: the partner's key table
    // maps 20 % to key 6 AND to key 20, so a key would be ambiguous where the rate never is.
    // A rate of ZERO is a stated rate and renders "0": the customer's tax-free intra-EU supplies
    // are taxed under such an entity (live taxKey AT_ADD_TAX_FREE_EU, taxValue "0"), and folding
    // them into an empty field would make them indistinguishable from a position that names no
    // tax entity at all.
    [Theory]
    [InlineData("3681", "20")]
    [InlineData("3682", "19")]
    [InlineData("3683", "0")]
    public void RenderPositions_VatField_IsTheRateInPercent(string taxId, string expected)
    {
        var o = new WeClappSalesOrder
        {
            Id = "1",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "1",
                    NetAmount = "10.00", GrossAmount = "12.00", TaxId = taxId,
                },
            },
        };

        Assert.Equal(expected, Field(DilosOrderWriter.RenderPositions(o, Ctx).Single(), 16));
    }

    // The shipping pseudo line is a position like any other and LKV prints it on the same
    // documents - WeClapp states its net, its gross and its taxId just as it does for an article
    // line, so leaving the four fields empty there would print an invoice line without a price.
    [Fact]
    public void RenderPositions_ShippingLine_CarriesItsOwnPricesAndRate()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            ShippingCostItems =
            {
                new WeClappShippingCostItem
                {
                    NetAmount = "4.50", GrossAmount = "5.40", TaxId = "3681",
                    Title = "DHL Standard (DE)",
                },
            },
        };

        var ship = DilosOrderWriter.RenderPositions(o, Ctx).Single();

        Assert.Equal("-1", Field(ship, 5));
        Assert.Equal("1", Field(ship, 11));   // the shipping line is always quantity 1
        Assert.Equal("20", Field(ship, 16));
        Assert.Equal("4.50", Field(ship, 18));
        Assert.Equal("4.50", Field(ship, 19));
        Assert.Equal("5.40", Field(ship, 20));
        Assert.Equal("5.40", Field(ship, 21));
    }

    // A position naming a tax entity the fetched /tax set does not contain must NOT render an
    // empty rate: empty is the legitimate value for a position that states no VAT at all, so the
    // delivered file could not be told apart from a correct one - and the AI delivery writes its
    // export marker on the way out, which makes the wrong file the final one for that order.
    // Failing here costs the next tick and no data.
    [Fact]
    public void RenderPositions_TaxIdOutsideTheFetchedRates_FailsLoudly()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "1",
                    NetAmount = "10.00", GrossAmount = "12.00", TaxId = "9999",
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, Ctx).ToList());

        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    // The rate is read with dot-decimal styles ONLY. Under NumberStyles.Any and InvariantCulture a
    // comma is a GROUP separator, so a rate of "13,5" would parse as 135 and, after the range
    // check, be delivered as a 135 percent VAT rate; "(20)" would parse as -20. Both are shapes a
    // number that came from the wrong place really has, and neither is a rate.
    [Theory]
    [InlineData("13,5")]     // comma decimal - would be 135 under NumberStyles.Any
    [InlineData("(20)")]     // accounting negative - would be -20 under NumberStyles.Any
    [InlineData("20%")]
    [InlineData("zwanzig")]
    [InlineData("")]
    [InlineData(null)]
    public void RenderPositions_TaxRateThatIsNotAPlainDecimal_FailsLoudly(string? taxValue)
    {
        var o = OrderTaxedUnder("T");

        var ex = Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, CtxWithTax("T", taxValue)).ToList());

        Assert.Contains("'T'", ex.Message, StringComparison.Ordinal);
    }

    // A percentage outside 0-100 did not come from a rate. Rounding it and shipping it would put a
    // number in field 16 that no DILOS import can mean anything by; zero and one hundred are both
    // real rates and stay legal.
    [Theory]
    [InlineData("-1", false)]
    [InlineData("101", false)]
    [InlineData("0", true)]
    [InlineData("100", true)]
    public void RenderPositions_TaxRateOutsideZeroToHundred_FailsLoudly(string taxValue, bool legal)
    {
        var o = OrderTaxedUnder("T");
        var ctx = CtxWithTax("T", taxValue);

        if (legal)
        {
            Assert.Equal(taxValue, Field(DilosOrderWriter.RenderPositions(o, ctx).Single(), 16));
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, ctx).ToList());
        Assert.Contains("0 to 100", ex.Message, StringComparison.Ordinal);
    }

    // The price fields are contract now, so an amount that is absent or unreadable fails the order.
    // The 0.00 stand-in predates that agreement: it cannot be told apart from a genuine zero, and
    // the AI delivery writes its export marker on the way out, so the wrong number would stand.
    // Both position kinds and both amounts go through the same builder and are covered here.
    [Theory]
    [InlineData(null, "12.00", "Positionspreis netto")]
    [InlineData("10.00", null, "Positionspreis brutto")]
    [InlineData("10,00", "12.00", "Positionspreis netto")]   // comma decimal: 1000 under Any
    [InlineData("10.00", "1 200", "Positionspreis brutto")]  // group separator: 1200 under Any
    public void RenderPositions_AmountMissingOrUnreadable_FailsLoudly(string? net, string? gross, string what)
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "1",
                    NetAmount = net, GrossAmount = gross, TaxId = "3681",
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, Ctx).ToList());

        Assert.Contains(what, ex.Message, StringComparison.Ordinal);
        Assert.Contains("position 1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4,50")]
    public void RenderPositions_ShippingAmountMissingOrUnreadable_FailsLoudly(string? net)
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            ShippingCostItems =
            {
                new WeClappShippingCostItem { NetAmount = net, GrossAmount = "5.40", TaxId = "3681" },
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, Ctx).ToList());
    }

    // The quantity is an input to two contract fields, not the pass-through text that field 11
    // makes it look like: 18 and 20 are the line amounts DIVIDED by it. A value that does not read
    // as a number used to answer 0, and PerUnit then stated the LINE amount as the unit price - on
    // a real quantity of 3 that is threefold, while 19 and 21 beside it stayed correct, so no sum
    // in the file disagrees and nothing downstream can notice. WeClappOrderItem.Quantity defaults
    // to "", which is what makes the empty case reachable rather than theoretical.
    [Theory]
    [InlineData("")]          // the model's own default
    [InlineData(null)]
    [InlineData("2,5")]       // comma decimal
    [InlineData("drei")]
    public void RenderPositions_QuantityMissingOrUnreadable_FailsLoudly(string? quantity)
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = quantity!,
                    NetAmount = "30.00", GrossAmount = "36.00", TaxId = "3681",
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => DilosOrderWriter.RenderPositions(o, Ctx).ToList());

        Assert.Contains("Mengeabg", ex.Message, StringComparison.Ordinal);
        Assert.Contains("position 1", ex.Message, StringComparison.Ordinal);
    }

    // A parseable "0" is a different statement from an unreadable one and keeps its documented
    // meaning: there is nothing to divide by, so the unit price IS the line amount.
    [Fact]
    public void RenderPositions_QuantityZero_KeepsTheLineAmountAsTheUnitPrice()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "0",
                    NetAmount = "30.00", GrossAmount = "36.00", TaxId = "3681",
                },
            },
        };

        var p = DilosOrderWriter.RenderPositions(o, Ctx).Single();

        Assert.Equal("0", Field(p, 11));      // the raw quantity still travels in field 11
        Assert.Equal("30.00", Field(p, 18));  // = the line amount, not a division by zero
        Assert.Equal("30.00", Field(p, 19));
        Assert.Equal("36.00", Field(p, 20));
        Assert.Equal("36.00", Field(p, 21));
    }

    // A price of genuinely zero is a legitimate statement and must keep rendering 0.00 - the
    // fail-loud above is about a MISSING amount, never about a cheap one.
    [Fact]
    public void RenderPositions_GenuineZeroPrice_StillRendersZero()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "2",
                    NetAmount = "0.00", GrossAmount = "0", TaxId = "3681",
                },
            },
        };

        var p = DilosOrderWriter.RenderPositions(o, Ctx).Single();

        Assert.Equal("20", Field(p, 16));
        Assert.Equal("0.00", Field(p, 18));
        Assert.Equal("0.00", Field(p, 19));
        Assert.Equal("0.00", Field(p, 20));
        Assert.Equal("0.00", Field(p, 21));
    }

    // The header total is an amount the file states as well, and it is read the same way.
    [Theory]
    [InlineData(null)]
    [InlineData("104,97")]
    public void RenderHeader_InvoiceTotalMissingOrUnreadable_FailsLoudly(string? grossAmount)
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",
            CustomerNumber = "7067387625809",
            GrossAmount = grossAmount,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => DilosOrderWriter.RenderHeader(o, Ctx));

        Assert.Contains("Rechnungssumme brutto", ex.Message, StringComparison.Ordinal);
    }

    private static WeClappSalesOrder OrderTaxedUnder(string taxId) => new()
    {
        Id = "5910986621265",
        OrderItems =
        {
            new WeClappOrderItem
            {
                PositionNumber = 1, ArticleId = "A1", Quantity = "1",
                NetAmount = "10.00", GrossAmount = "12.00", TaxId = taxId,
            },
        },
    };

    // A position that names no tax entity states no rate - the field stays empty, which the
    // partner's own files show is importable. The prices do not depend on the rate (WeClapp
    // states the gross itself), so they are still filled.
    [Fact]
    public void RenderPositions_PositionWithoutTaxId_LeavesTheRateEmptyAndKeepsThePrices()
    {
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1", Quantity = "1",
                    NetAmount = "10.00", GrossAmount = "12.00",
                },
            },
        };

        var p = DilosOrderWriter.RenderPositions(o, Ctx).Single();

        Assert.Equal("", Field(p, 16));
        Assert.Equal("10.00", Field(p, 18));
        Assert.Equal("10.00", Field(p, 19));
        Assert.Equal("12.00", Field(p, 20));
        Assert.Equal("12.00", Field(p, 21));
    }

    [Fact]
    public void RenderPositions_NonContiguousWeClappPositionNumbers_ProducesUniqueSequentialPositions()
    {
        // WeClapp positionNumber can have gaps (deleted lines, manual numbering).
        // DILOS requires "Position eindeutig pro Auftragsnummer" — a WeClapp
        // positionNumber of 3 must not collide with the shipping pseudo line
        // that is appended as the 3rd rendered row.
        var o = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "1015",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "A1",
                    Quantity = "1", NetAmount = "10.00", GrossAmount = "12.00", Title = "Item eins"
                },
                new WeClappOrderItem
                {
                    PositionNumber = 3, ArticleId = "A2",
                    Quantity = "2", NetAmount = "20.00", GrossAmount = "24.00", Title = "Item zwei"
                }
            },
            ShippingCostItems =
            {
                new WeClappShippingCostItem
                {
                    NetAmount = "4.50", GrossAmount = "5.40", Title = "DHL Standard (DE)",
                },
            }
        };

        var lines = DilosOrderWriter.RenderPositions(o, Ctx).ToList();

        Assert.Equal(3, lines.Count);
        var positions = lines.Select(l => Field(l, 3)).ToList();
        var deliveryNotePositions = lines.Select(l => Field(l, 4)).ToList();
        Assert.Equal(new[] { "1", "2", "3" }, positions);
        Assert.Equal(positions, deliveryNotePositions);
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }
}
