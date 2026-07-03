using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosFormatTests
{
    [Fact]
    public void DataLines_NumbersPhysicalLinesAndSkipsEmpty()
    {
        var lines = DilosFormat.DataLines("a\r\n\r\nb\r\n").ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(("a", 1), lines[0]);
        Assert.Equal(("b", 3), lines[1]);
    }

    [Theory]
    [InlineData("2,5", 2.5)]
    [InlineData("-1", -1)]
    [InlineData("108", 108)]
    public void Dec_ParsesCommaDecimalAndSign(string raw, decimal expected)
    {
        Assert.Equal(expected, DilosFormat.Dec(raw, 7, "Menge"));
    }

    [Theory]
    [InlineData("2.5")]   // dot is NOT the AR/BE decimal separator — must fail loud
    [InlineData("1.000")] // no thousands separators either
    [InlineData("")]
    [InlineData("x")]
    public void Dec_ThrowsWithLineNumberOnInvalid(string raw)
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosFormat.Dec(raw, 7, "Menge"));

        Assert.Equal(7, ex.LineNumber);
        Assert.Contains("Line 7", ex.Message);
        Assert.Contains("Menge", ex.Message);
    }

    [Fact]
    public void OptDec_EmptyIsNull() => Assert.Null(DilosFormat.OptDec("", 1, "f"));

    [Fact]
    public void OptInt_ParsesAndEmptyIsNull()
    {
        Assert.Equal(3, DilosFormat.OptInt("3", 1, "f"));
        Assert.Null(DilosFormat.OptInt("", 1, "f"));
        Assert.Throws<DilosParseException>(() => DilosFormat.OptInt("3,5", 1, "f"));
    }

    [Fact]
    public void OptDate_ParsesGermanFormatExactly()
    {
        Assert.Equal(new DateOnly(2024, 4, 10), DilosFormat.OptDate("10.04.2024", 1, "Datum"));
        Assert.Null(DilosFormat.OptDate("", 1, "Datum"));
        Assert.Throws<DilosParseException>(() => DilosFormat.OptDate("2024-04-10", 1, "Datum"));
    }
}
