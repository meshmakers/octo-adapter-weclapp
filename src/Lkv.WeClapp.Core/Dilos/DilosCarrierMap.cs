namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Maps DILOS carrier codes (AR C* field 3, project-defined table 100–800) to WeClapp
/// ecommerceShippingCarrier constants used on the shippingCarrier entity. Only the four
/// carriers present in the golden AR files are mapped; unknown codes (golden reality
/// includes a code "9" outside the spec table) map to nothing — the caller writes
/// tracking number/URL without a carrier reference and logs the gap.
/// </summary>
public static class DilosCarrierMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["100"] = "AUSTRIAN_POST",
        ["200"] = "UPS",
        ["400"] = "DHL",
        ["800"] = "DPD"
    };

    public static bool TryMap(string dilosCarrierCode, out string? ecommerceShippingCarrier)
    {
        if (Map.TryGetValue(dilosCarrierCode.Trim(), out var value))
        {
            ecommerceShippingCarrier = value;
            return true;
        }

        ecommerceShippingCarrier = null;
        return false;
    }
}
