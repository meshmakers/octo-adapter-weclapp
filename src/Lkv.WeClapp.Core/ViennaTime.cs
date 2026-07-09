namespace Lkv.WeClapp.Core;

/// <summary>
/// Europe/Vienna date anchoring for both transfer directions. WeClapp normalises
/// date-picker fields (orderDate, shippingDate, invoiceDate, …) to ACCOUNT-LOCAL
/// midnight — raw customer-system values are 22:00Z (CEST) / 23:00Z (CET) of the
/// previous day, never 00:00Z. DILOS dates are Austrian local. Converting through
/// UTC would therefore shift such values to the previous calendar day.
/// </summary>
public static class ViennaTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    /// <summary>Austrian-local calendar date of a WeClapp epoch-ms timestamp.</summary>
    public static DateOnly ToViennaDate(long epochMs) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs), Zone).DateTime);

    /// <summary>Epoch ms of Vienna midnight for an Austrian-local date.</summary>
    public static long ToEpochMsAtViennaMidnight(DateOnly date)
    {
        var localMidnight = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(localMidnight, Zone.GetUtcOffset(localMidnight))
            .ToUnixTimeMilliseconds();
    }
}
