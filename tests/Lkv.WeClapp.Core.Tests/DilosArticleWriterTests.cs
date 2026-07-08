using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

public class DilosArticleWriterTests
{
    // DILOS fields are 1-indexed (A* = field 1); C# Split is 0-indexed.
    private static string Field(string line, int dilosFieldNo) => line.Split('|')[dilosFieldNo - 1];

    [Fact]
    public void RenderLine_MapsKeyFields_EkZeroWhenMissing()
    {
        var art = new WeClappArticle
        {
            Id = "4262",
            Name = "TestArtikel",
            ArticleNumber = "000123",
            UnitName = "kg",
            ArticleType = "STORABLE",
            Ean = "9120103151353"
            // no SupplySources → PurchasePrice null
        };

        var line = DilosArticleWriter.RenderLine(art, DilosArticleContext.Default);

        Assert.Equal("A*", Field(line, 1));
        Assert.Equal("4262", Field(line, 3));            // Artikelnummer = WeClapp id
        Assert.Equal("TestArtikel", Field(line, 4));     // Bezeichnung1
        Assert.Equal("000123", Field(line, 5));          // Bezeichnung2 = SKU
        Assert.Equal("9120103151353", Field(line, 11));  // EAN
        Assert.Equal("kg", Field(line, 12));             // Lagereinheit
        Assert.Equal("0", Field(line, 20));              // EK-Preis null -> 0 (Jürgen 2026-06-29)
        Assert.Equal("1", Field(line, 23));              // Artikelart
        Assert.Equal("", Field(line, 24));               // MwSt-Schlüssel leer
        Assert.Equal("", Field(line, 25));               // Währung leer
    }

    [Fact]
    public void RenderLine_EkValue_PassesThrough()
    {
        var art = new WeClappArticle
        {
            Id = "1", ArticleType = "STORABLE",
            SupplySources = { new WeClappSupplySource { ArticlePrices = { new WeClappArticlePrice { Price = "9.99" } } } }
        };
        var line = DilosArticleWriter.RenderLine(art, DilosArticleContext.Default);
        Assert.Equal("9.99", Field(line, 20));
    }

    [Fact]
    public void RenderLine_Has34Fields()
    {
        var line = DilosArticleWriter.RenderLine(new WeClappArticle { ArticleType = "STORABLE" }, DilosArticleContext.Default);
        Assert.Equal(34, line.Split('|').Length);
    }
}
