using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosCarrierMapTests
{
    [Theory]
    [InlineData("100", "AUSTRIAN_POST")]
    [InlineData("200", "UPS")]
    [InlineData("300", "GLS")] // GLS IS in the WeClapp enum (OpenAPI/SDK ground truth)
    [InlineData("400", "DHL")]
    [InlineData("800", "DPD")]
    public void TryMap_KnownGoldenCodes_ReturnsEcommerceCarrier(string dilosCode, string expected)
    {
        Assert.True(DilosCarrierMap.TryMap(dilosCode, out var carrier));
        Assert.Equal(expected, carrier);
    }

    [Theory]
    [InlineData("9")]    // initial placeholder without meaning (LKV 2026-07-16); 100 is Post
    [InlineData("")]
    [InlineData("abc")]
    public void TryMap_UnknownOrUnsupportedCodes_ReturnsFalse(string dilosCode)
    {
        Assert.False(DilosCarrierMap.TryMap(dilosCode, out var carrier));
        Assert.Null(carrier);
    }

    [Theory]
    [InlineData("300", "GLS")] // ALSO name-resolvable: tenant carrier entities may lack the
                               // ecommerce constant (the pilot's GLS entity does) — then the
                               // node falls back to matching the display name
    public void TryMapName_CodesWithNameFallback_ReturnsCarrierName(string dilosCode, string expected)
    {
        Assert.True(DilosCarrierMap.TryMapName(dilosCode, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("100")] // no name fallback defined — constant-only codes
    [InlineData("9")]
    [InlineData("")]
    [InlineData("abc")]
    public void TryMapName_OtherCodes_ReturnsFalse(string dilosCode)
    {
        Assert.False(DilosCarrierMap.TryMapName(dilosCode, out var name));
        Assert.Null(name);
    }
}
