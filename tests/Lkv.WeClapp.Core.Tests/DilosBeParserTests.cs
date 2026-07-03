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

    [Fact]
    public void Parse_WrongFieldCountThrows_IncludingBillbee7FieldVariant()
    {
        // Billbee's CsvBestandsmeldung expects 7 fields (extra SKU column) — the LKV
        // spec + all 1114 golden lines have 6. If the new customer ever gets the
        // 7-field variant, this must surface immediately, not parse shifted.
        var sevenFields = "A1|SKU-1|0|0||5|VER\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosBeParser.Parse(sevenFields));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("expected 6", ex.Message);
    }

    [Fact]
    public void Parse_EmptyQuantityThrows()
    {
        Assert.Throws<DilosParseException>(() => DilosBeParser.Parse("A1|0|0|||VER\r\n"));
    }
}
