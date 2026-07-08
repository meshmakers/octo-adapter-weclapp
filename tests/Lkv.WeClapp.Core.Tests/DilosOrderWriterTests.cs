using System.Globalization;
using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

public class DilosOrderWriterTests
{
    private static string Field(string line, int dilosFieldNo) => line.Split('|')[dilosFieldNo - 1];
    private static readonly DilosOrderContext Ctx = new() { Submandant = "51696697501" };

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
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(orderDate).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            Field(k, 26));                            // Auftragsdatum
        Assert.Equal("5910986621265", Field(k, 30));  // Auftragsnummer1
        Assert.Equal("74299", Field(k, 31));          // Auftragsnummer2
        Assert.Equal("3415", Field(k, 33));           // Frächter = WeClapp shipmentMethod-ID (Jürgen 2026-06-28)
        Assert.Equal("0", Field(k, 46));              // Text4: kein Rechnungsdruck
        Assert.Equal("104.97", Field(k, 65));         // RechnungssummeBrutto
        Assert.Equal(66, k.Split('|').Length);
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
