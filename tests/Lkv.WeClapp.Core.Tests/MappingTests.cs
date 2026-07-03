using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

public class MappingTests
{
    [Theory]
    [InlineData("EUR", 5)]
    public void Currency_Maps(string name, int code) => Assert.Equal(code, WeClappToDilos.Currency(name));

    [Fact]
    public void Currency_Unknown_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WeClappToDilos.Currency("USD"));

    [Fact]
    public void Ek_Null_IsZero() => Assert.Equal(0m, WeClappToDilos.EkPreis(null));

    [Fact]
    public void Ek_Value_PassesThrough() => Assert.Equal(9.99m, WeClappToDilos.EkPreis(9.99m));

    [Theory]
    [InlineData(29.99, 2, "29.99")]
    [InlineData(15, 3, "15.000")]
    [InlineData(0, 2, "0.00")]
    public void Dec_FixedDecimalsInvariant(decimal v, int d, string e) => Assert.Equal(e, WeClappToDilos.Dec(v, d));

    [Fact]
    public void MwSt_Rounds() => Assert.Equal(20, WeClappToDilos.MwStPercent(20m));

    [Fact]
    public void SystemArticle_LoadingEquipment() =>
        Assert.True(WeClappToDilos.IsSystemArticle(new WeClappArticle { ArticleType = "LOADING_EQUIPMENT" }));

    [Fact]
    public void NormalArticle_NotSystem() =>
        Assert.False(WeClappToDilos.IsSystemArticle(new WeClappArticle { ArticleType = "STORABLE" }));

    [Fact]
    public void SystemCustomer_AnonymousDebitor() =>
        Assert.True(WeClappToDilos.IsSystemCustomer("ANONYMOUS_DEBITOR"));
}
