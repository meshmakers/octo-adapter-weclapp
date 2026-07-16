namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// TRANSITIONAL FALLBACK mapping of DILOS legacy carrier codes (AR C* field 3) to WeClapp
/// carriers. The primary path (since 2026-07-08): C* field 3 carries the carrier id as
/// configured in the shop system — for WeClapp the shippingCarrier entity id itself, which
/// the AR write node resolves directly against the live carrier list BEFORE consulting
/// these tables. LKV keeps accepting/sending the legacy table codes during the transition
/// (2026-07-16): 100 = Austrian Post, 200 = UPS, 300 = GLS, 400 = DHL, 800 = DPD.
/// "9" appears in old golden files but was an initial placeholder without meaning — it maps
/// to nothing. GLS has no WeClapp ecommerceShippingCarrier constant, so 300 resolves via the
/// carrier NAME against the live list instead. Unresolvable tokens: tracking is written
/// without a carrier reference.
/// </summary>
public static class DilosCarrierMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["100"] = "AUSTRIAN_POST",
        ["200"] = "UPS",
        ["400"] = "DHL",
        ["800"] = "DPD",
    };

    private static readonly Dictionary<string, string> NameMap = new(StringComparer.Ordinal)
    {
        ["300"] = "GLS", // no WeClapp ecommerce constant — matched against the carrier name
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

    public static bool TryMapName(string dilosCarrierCode, out string? carrierName)
    {
        if (NameMap.TryGetValue(dilosCarrierCode.Trim(), out var value))
        {
            carrierName = value;
            return true;
        }

        carrierName = null;
        return false;
    }
}
