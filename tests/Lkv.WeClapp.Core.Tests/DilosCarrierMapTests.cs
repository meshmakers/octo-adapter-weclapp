using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosCarrierMapTests
{
    [Theory]
    [InlineData("100", "AUSTRIAN_POST")]
    [InlineData("200", "UPS")]
    [InlineData("400", "DHL")]
    [InlineData("800", "DPD")]
    public void TryMap_KnownGoldenCodes_ReturnsEcommerceCarrier(string dilosCode, string expected)
    {
        Assert.True(DilosCarrierMap.TryMap(dilosCode, out var carrier));
        Assert.Equal(expected, carrier);
    }

    [Theory]
    [InlineData("300")]  // GLS has no WeClapp ecommerce constant — resolved by NAME instead
    [InlineData("9")]    // initial placeholder without meaning (Jürgen 2026-07-16); 100 is Post
    [InlineData("")]
    [InlineData("abc")]
    public void TryMap_UnknownOrUnsupportedCodes_ReturnsFalse(string dilosCode)
    {
        Assert.False(DilosCarrierMap.TryMap(dilosCode, out var carrier));
        Assert.Null(carrier);
    }

    [Theory]
    [InlineData("300", "GLS")] // transition code in real LKV ARs (Jürgen 2026-07-16) — WeClapp
                               // has no GLS constant, so the node matches the carrier NAME
    public void TryMapName_CodesWithoutConstant_ReturnsCarrierName(string dilosCode, string expected)
    {
        Assert.True(DilosCarrierMap.TryMapName(dilosCode, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("100")] // has a constant — the name path is only for constant-less carriers
    [InlineData("9")]
    [InlineData("")]
    [InlineData("abc")]
    public void TryMapName_OtherCodes_ReturnsFalse(string dilosCode)
    {
        Assert.False(DilosCarrierMap.TryMapName(dilosCode, out var name));
        Assert.Null(name);
    }
}
