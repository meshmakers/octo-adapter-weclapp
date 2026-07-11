namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Extracted tracking information from a DILOS "Paketnummer" (AR C* field 4) value.
/// </summary>
public sealed record TrackingInfo
{
    /// <summary>Distinct tracking numbers in original order. Golden AR files always yield exactly one.</summary>
    public IReadOnlyList<string> Numbers { get; init; } = [];

    /// <summary>The raw carrier tracking URL when the value was a URL, otherwise null (bare number, golden carrier code 9).</summary>
    public string? Url { get; init; }
}

/// <summary>
/// Splits DILOS "Paketnummer" values into tracking numbers. Golden reality (verified over all
/// 103 parcels of the LKV test files): 102 values are carrier URLs whose query payload contains
/// ONE tracking number duplicated after a comma ("...=X,X"; DPD/DHL/Post/UPS shapes), one value
/// is a bare number without URL — so this deduplicates rather than splits. Genuinely distinct
/// numbers (never seen in goldens) are all kept, in order; the caller decides whether &gt;1 is
/// worth a warning.
/// </summary>
public static class TrackingSplitter
{
    public static TrackingInfo Split(string trackingValue)
    {
        var value = trackingValue.Trim();
        if (value.Length == 0)
        {
            return new TrackingInfo();
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            return new TrackingInfo { Numbers = Tokenize(value) };
        }

        // All golden URLs carry the number(s) as the last query value; anything before
        // the final '=' is carrier boilerplate.
        var lastEquals = value.LastIndexOf('=');
        if (lastEquals < 0 || lastEquals == value.Length - 1)
        {
            return new TrackingInfo { Url = value };
        }

        return new TrackingInfo
        {
            Numbers = Tokenize(value[(lastEquals + 1)..]),
            Url = value
        };
    }

    private static List<string> Tokenize(string payload) =>
        payload.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
