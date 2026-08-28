using System.Globalization;
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosFileTests
{
    [Fact]
    public void AiFileName_UsesAuftragsnummer1Verbatim()
    {
        // Golden precedent AI5910748889425.txt: name = "AI" + Auftragsnummer1 (WeClapp id).
        Assert.Equal("AI5910986621265.txt", DilosFile.AiFileName("5910986621265"));
    }

    [Fact]
    public void DeliveryFileName_StampsViennaLocalTime_Cet()
    {
        // 2026-02-05 13:31:34 UTC = 14:31:34 Vienna (CET, UTC+1); golden 14-digit format.
        Assert.Equal("AS20260205143134.txt",
            DilosFile.DeliveryFileName("AS", new DateTimeOffset(2026, 2, 5, 13, 31, 34, TimeSpan.Zero)));
    }

    [Fact]
    public void DeliveryFileName_LateEveningUtcRollsToNextViennaDay_Cest()
    {
        // 2026-07-11 22:30 UTC = 2026-07-12 00:30 Vienna (CEST) — name carries the NEXT day.
        Assert.Equal("AS20260712003000.txt",
            DilosFile.DeliveryFileName("AS", new DateTimeOffset(2026, 7, 11, 22, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void DeliveryFileName_IsCultureInvariant()
    {
        // th-TH defaults to the Thai Buddhist calendar (year + 543) — the golden stamp
        // must stay Gregorian regardless of the process culture.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("AS20260205143134.txt",
                DilosFile.DeliveryFileName("AS", new DateTimeOffset(2026, 2, 5, 13, 31, 34, TimeSpan.Zero)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Encoding_EncodesLatin1AsSingleBytes_AndReplacesOutsideLatin1()
    {
        // ü must land as ONE Latin-1 byte (0xFC), € (U+20AC, not in Latin-1) as '?'.
        Assert.Equal(new byte[] { 0xFC }, DilosFile.Encoding.GetBytes("ü"));
        Assert.Equal((byte)'?', DilosFile.Encoding.GetBytes("€")[0]);
    }
}
