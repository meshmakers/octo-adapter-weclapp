namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// LEGACY FALLBACK mapping of DILOS/Billbee-era carrier codes (AR C* field 3) to WeClapp
/// ecommerceShippingCarrier constants. Since Jürgen's answer of 2026-07-08, C* field 3
/// primarily carries the carrier id as configured in the shop system — for WeClapp the
/// shippingCarrier entity id itself, which the AR write node resolves directly against
/// the live carrier list BEFORE consulting this table. This map only covers the codes
/// found in the golden files: the spec table 100–800 plus "9", the Billbee-internal id
/// LKV returned for ÖPAG back then. Unresolvable tokens: tracking is written without a
/// carrier reference.
/// </summary>
public static class DilosCarrierMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["100"] = "AUSTRIAN_POST",
        ["200"] = "UPS",
        ["400"] = "DHL",
        ["800"] = "DPD",
        ["9"] = "AUSTRIAN_POST" // ÖPAG via its Billbee-era shop-system id (Jürgen 2026-07-08)
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
