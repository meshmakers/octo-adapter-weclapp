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
    [InlineData("9")]    // golden reality: occurs once, outside the spec table
    [InlineData("300")]  // spec lists GLS, but WeClapp has no GLS ecommerce carrier constant
    [InlineData("")]
    [InlineData("abc")]
    public void TryMap_UnknownOrUnsupportedCodes_ReturnsFalse(string dilosCode)
    {
        Assert.False(DilosCarrierMap.TryMap(dilosCode, out var carrier));
        Assert.Null(carrier);
    }
}
