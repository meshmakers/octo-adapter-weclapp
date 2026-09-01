using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

public class DilosOrderWriterTests
{
    private static string Field(string line, int dilosFieldNo) => line.Split('|')[dilosFieldNo - 1];

    // The rate a position states comes from the WeClapp /tax entity its taxId points at; 3681 is
    // the id the committed salesOrder fixture references.
    private static readonly DilosOrderContext Ctx = new()
    {
        Submandant = "51696697501",
        TaxRatePercentById = new Dictionary<string, int> { ["3681"] = 20, ["3682"] = 19 },
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
                    Quantity = "1", NetAmount = "29.99", Title = "Ersatzglas VOLT"
                }
            },
            ShippingCostItems = { new WeClappShippingCostItem { NetAmount = "4.50", Title = "DHL Standard (DE)" } }
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
    [Theory]
    [InlineData("3681", "20")]
    [InlineData("3682", "19")]
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
                    Quantity = "1", NetAmount = "10.00", Title = "Item eins"
                },
                new WeClappOrderItem
                {
                    PositionNumber = 3, ArticleId = "A2",
                    Quantity = "2", NetAmount = "20.00", Title = "Item zwei"
                }
            },
            ShippingCostItems = { new WeClappShippingCostItem { NetAmount = "4.50", Title = "DHL Standard (DE)" } }
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
