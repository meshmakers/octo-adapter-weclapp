using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosBeParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void Parse_ReadsGoldenStockLine()
    {
        // First line of BE_20240205035403463.txt: 39287037853853|0|0||108|VER
        var lines = DilosBeParser.Parse(Fixture("BE_20240205035403463.txt"));

        var first = lines[0];
        Assert.Equal("39287037853853", first.ArticleNumber);
        Assert.Equal("0", first.Characteristic1);
        Assert.Equal("0", first.Characteristic2);
        Assert.Equal("", first.LotNumber);
        Assert.Equal(108m, first.Quantity);
        Assert.Equal(DilosStockStatus.Available, first.Status);
    }

    [Theory]
    [InlineData("BE_20240205035403463.txt")]
    [InlineData("BE_20240206035402497.txt")]
    [InlineData("BE_20240410153954163.txt")]
    public void Parse_AllGoldenFilesRoundTripLineCounts(string file)
    {
        var content = Fixture(file);
        var nonEmptyLines = content.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);

        var lines = DilosBeParser.Parse(content);

        Assert.Equal(nonEmptyLines, lines.Count);
        Assert.All(lines, l => Assert.NotEqual("", l.ArticleNumber));
    }

    [Fact]
    public void Parse_MapsGesToBlocked()
    {
        var lines = DilosBeParser.Parse("A1|0|0||5|GES\r\n");

        Assert.Equal(DilosStockStatus.Blocked, lines[0].Status);
    }

    [Fact]
    public void Parse_UnknownStatusThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosBeParser.Parse("A1|0|0||5|XXX\r\n"));

        Assert.Equal(1, ex.LineNumber);
    }

    /// <summary>
    /// This parser used to reject the 7-field variant on purpose, so that it would surface rather
    /// than parse shifted if it ever arrived. It arrived, and the extra column is exactly what the
    /// guard predicted: the SKU, in second position. Both layouts are live now - the 6-field one in
    /// the LKV spec and in 1114 golden lines, the 7-field one in what the customer sends today - so
    /// both are read, told apart by the field count alone.
    /// </summary>
    [Fact]
    public void Parse_SevenFieldVariant_ReadsTheSkuAndShiftsNothing()
    {
        var lines = DilosBeParser.Parse("A1|SKU-1|c1|c2|LOT|5|VER\r\n");

        var line = lines[0];
        Assert.Equal("A1", line.ArticleNumber);
        Assert.Equal("SKU-1", line.ArticleCode);
        Assert.Equal("c1", line.Characteristic1);
        Assert.Equal("c2", line.Characteristic2);
        Assert.Equal("LOT", line.LotNumber);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(DilosStockStatus.Available, line.Status);
    }

    /// <summary>The 6-field layout carries no SKU; the remaining fields keep their meaning.</summary>
    [Fact]
    public void Parse_SixFieldVariant_LeavesTheArticleCodeEmpty()
    {
        var lines = DilosBeParser.Parse("A1|c1|c2|LOT|5|VER\r\n");

        var line = lines[0];
        Assert.Equal("A1", line.ArticleNumber);
        Assert.Equal("", line.ArticleCode);
        Assert.Equal("c1", line.Characteristic1);
        Assert.Equal("c2", line.Characteristic2);
        Assert.Equal("LOT", line.LotNumber);
        Assert.Equal(5m, line.Quantity);
    }

    [Theory]
    [InlineData("A1|0|0||5\r\n")]
    [InlineData("A1|SKU|0|0||5|VER|extra\r\n")]
    public void Parse_AnyOtherFieldCountThrows(string content)
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosBeParser.Parse(content));

        Assert.Equal(1, ex.LineNumber);
    }

    /// <summary>
    /// The real file the customer sends is CRLF terminated, so the record separator must not end up
    /// inside the last field: a status of "VER\r" would fail the enum mapping, and a stray carriage
    /// return anywhere else would travel onward as part of a value.
    /// </summary>
    [Fact]
    public void Parse_RealCustomerFile_ReadsEveryLineWithoutCarriageReturns()
    {
        var content = Fixture("BE_20260828071116067.txt");

        // The fixture has to still BE carriage-return terminated for the rest of this test to mean
        // anything. Line-ending normalisation on the way into the repository would strip them and
        // leave every assertion below trivially true, which is a hollow test rather than a failing
        // one - so the premise is checked instead of assumed.
        Assert.Contains("\r\n", content, StringComparison.Ordinal);
        Assert.Equal(46, content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var lines = DilosBeParser.Parse(content);

        Assert.Equal(46, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.DoesNotContain('\r', l.ArticleNumber);
            Assert.DoesNotContain('\r', l.ArticleCode);
            Assert.Equal(DilosStockStatus.Available, l.Status);
        });

        // First line: 155294|TS_001|0|0||15|VER
        var first = lines[0];
        Assert.Equal("155294", first.ArticleNumber);
        Assert.Equal("TS_001", first.ArticleCode);
        Assert.Equal("0", first.Characteristic1);
        Assert.Equal("0", first.Characteristic2);
        Assert.Equal("", first.LotNumber);
        Assert.Equal(15m, first.Quantity);
    }

    /// <summary>
    /// The match key the write side uses is field 1, and the extra column must not displace it:
    /// every article number in the real file is one this adapter itself delivered in an AS file,
    /// while the SKU beside it is the AS description field.
    /// </summary>
    [Fact]
    public void Parse_RealCustomerFile_KeepsTheArticleNumberAsTheFirstField()
    {
        var lines = DilosBeParser.Parse(Fixture("BE_20260828071116067.txt"));

        Assert.All(lines, l => Assert.Matches("^[0-9]+$", l.ArticleNumber));
        Assert.All(lines, l => Assert.NotEqual(l.ArticleNumber, l.ArticleCode));
        Assert.Contains(lines, l => l.ArticleNumber == "4269");
    }

    [Fact]
    public void Parse_EmptyQuantityThrows()
    {
        Assert.Throws<DilosParseException>(() => DilosBeParser.Parse("A1|0|0|||VER\r\n"));
    }
}
