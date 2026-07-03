using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosArParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    // Golden AR00006946.TXT line 1:
    // K*|1|1|400000001572890||400000001247987|TEST-123|1001801714|1400137|2|10.04.2024|1|1|2,5
    [Fact]
    public void Parse_ReadsGoldenHeader()
    {
        var shipments = DilosArParser.Parse(Fixture("AR00006946.TXT"));

        var s = Assert.Single(shipments);
        Assert.Equal("1", s.Division);
        Assert.Equal("1", s.ClientId);
        Assert.Equal("400000001572890", s.InvoiceClientId);
        Assert.Equal("", s.Zone);
        Assert.Equal("400000001247987", s.OrderNumber1);
        Assert.Equal("TEST-123", s.OrderNumber2);
        Assert.Equal("1001801714", s.DilosOrderNumber);
        Assert.Equal("1400137", s.DilosForwardingNumber);
        Assert.Equal("2", s.Difference);
        Assert.Equal(new DateOnly(2024, 4, 10), s.ShipmentDate);
        Assert.Equal(1m, s.TotalQuantity);
        Assert.Equal(1, s.ParcelCount);
        Assert.Equal(2.5m, s.TotalWeight);
    }

    [Fact]
    public void Parse_HeaderWithWrongFieldCountThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse("K*|1|2\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("expected 14", ex.Message);
    }

    [Fact]
    public void Parse_UnknownPrefixThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse("X*|foo\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("X*", ex.Message);
    }

    // Golden AR00006946.TXT lines 2-4:
    // C*|400000001247987|9|1013408501850970172035|Karton|Standard|2,5
    // P*|400000001247987|1|400000001273682||||||0|1|-1
    // L*|400000001247987|1|400000001273682||||||1|1013408501850970172035
    [Fact]
    public void Parse_ReadsGoldenParcelItemAndPackingLine()
    {
        var s = Assert.Single(DilosArParser.Parse(Fixture("AR00006946.TXT")));

        var parcel = Assert.Single(s.Parcels);
        Assert.Equal("400000001247987", parcel.OrderNumber1);
        Assert.Equal("9", parcel.Carrier);
        Assert.Equal("1013408501850970172035", parcel.TrackingNumber);
        Assert.Equal("Karton", parcel.PackagingType);
        Assert.Equal("Standard", parcel.ServiceType);
        Assert.Equal(2.5m, parcel.Weight);

        var item = Assert.Single(s.Items);
        Assert.Equal(1, item.PositionNumber);
        Assert.Equal("400000001273682", item.ArticleNumber);
        Assert.Equal("", item.PartCondition);
        Assert.Equal(0m, item.OrderedQuantity);
        Assert.Equal(1m, item.DeliveredQuantity);
        Assert.Equal(-1m, item.OpenQuantity); // over-delivery, golden-verified

        var packing = Assert.Single(s.PackingLines);
        Assert.Equal(1, packing.PositionNumber);
        Assert.Equal("400000001273682", packing.ArticleNumber);
        Assert.Equal(1m, packing.PackedQuantity);
        Assert.Equal("1013408501850970172035", packing.TrackingNumber);
    }

    [Fact]
    public void Parse_SubRecordBeforeHeaderThrows()
    {
        var ex = Assert.Throws<DilosParseException>(
            () => DilosArParser.Parse("C*|123|800|T1|Karton|Standard|1,0\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("before first K*", ex.Message);
    }

    [Fact]
    public void Parse_SubRecordOrderNumberMismatchThrows()
    {
        var content =
            "K*|1|1|||ORDER-A||1|1|0|10.04.2024|1|1|1,0\r\n" +
            "C*|ORDER-B|800|T1|Karton|Standard|1,0\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse(content));

        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("ORDER-B", ex.Message);
        Assert.Contains("ORDER-A", ex.Message);
    }

    [Fact]
    public void Parse_SubRecordWrongFieldCountThrows()
    {
        var content =
            "K*|1|1|||ORDER-A||1|1|0|10.04.2024|1|1|1,0\r\n" +
            "P*|ORDER-A|1|ART|x\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse(content));

        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("expected 12", ex.Message);
    }

    // awk-verified 2026-07-03 on AR20240205143134947.TXT: 33x K*, 33x C*, 92x P*, 59x L*
    [Fact]
    public void Parse_MultiShipmentFileGroupsAllRecords()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        Assert.Equal(33, shipments.Count);
        Assert.Equal(33, shipments.Sum(s => s.Parcels.Count));
        Assert.Equal(92, shipments.Sum(s => s.Items.Count));
        Assert.Equal(59, shipments.Sum(s => s.PackingLines.Count));
        Assert.All(shipments, s => Assert.NotEqual("", s.OrderNumber1));
    }

    // Golden: C* TrackingNumber can be a carrier URL. In all 102 golden URLs the single
    // tracking number appears DUPLICATED after a comma (p=X,X style) — the parser must
    // keep it raw, no splitting (a later splitter must dedupe).
    [Fact]
    public void Parse_KeepsTrackingUrlRaw()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        var first = shipments[0]; // K* OrderNumber1 5905280991569, C* carrier 800 (DPD)
        var parcel = Assert.Single(first.Parcels);
        Assert.Equal("800", parcel.Carrier);
        Assert.StartsWith("http://www.mydpd.at/", parcel.TrackingNumber);
        Assert.Contains(",", parcel.TrackingNumber); // the duplicated-number comma stays raw
    }

    // Golden: P*|5905280991569|3|||||||1|1|0 — empty ArticleNumber is valid data.
    [Fact]
    public void Parse_EmptyItemArticleNumberIsKeptNotRejected()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        var withEmpty = shipments[0].Items.Single(i => i.PositionNumber == 3);
        Assert.Equal("", withEmpty.ArticleNumber);
        Assert.Equal(1m, withEmpty.DeliveredQuantity);
    }

    // Golden (ultracode-verified 2026-07-03): K* Gesamtmenge equals the sum of ALL P*
    // DeliveredQuantity INCLUDING the empty-ArticleNumber shipping pseudo-item — 103/103
    // shipments across all five files. Pins the TotalQuantity semantics.
    [Theory]
    [InlineData("AR00006946.TXT")]
    [InlineData("AR20240205143134947.TXT")]
    [InlineData("AR20240205150134383.TXT")]
    [InlineData("AR20240206080135220.TXT")]
    [InlineData("AR20240206083134910.TXT")]
    public void Parse_TotalQuantityEqualsSumOfAllItemDeliveredQuantities(string file)
    {
        var shipments = DilosArParser.Parse(Fixture(file));

        Assert.All(shipments, s =>
        {
            Assert.NotNull(s.TotalQuantity);
            Assert.Equal(s.TotalQuantity!.Value, s.Items.Sum(i => i.DeliveredQuantity));
        });
    }

    [Theory]
    [InlineData("AR00006946.TXT")]
    [InlineData("AR20240205143134947.TXT")]
    [InlineData("AR20240205150134383.TXT")]
    [InlineData("AR20240206080135220.TXT")]
    [InlineData("AR20240206083134910.TXT")]
    public void Parse_AllGoldenFilesParseCleanly(string file)
    {
        var shipments = DilosArParser.Parse(Fixture(file));

        Assert.NotEmpty(shipments);
        Assert.All(shipments, s => Assert.NotEmpty(s.Parcels));
    }
}
