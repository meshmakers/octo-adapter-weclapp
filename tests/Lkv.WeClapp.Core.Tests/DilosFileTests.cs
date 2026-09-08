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
    public void Encoding_EncodesLatin1AsSingleBytes_AndReplacesOutsideLatin1()
    {
        // ü must land as ONE Latin-1 byte (0xFC), € (U+20AC, not in Latin-1) as '?'.
        Assert.Equal(new byte[] { 0xFC }, DilosFile.Encoding.GetBytes("ü"));
        Assert.Equal((byte)'?', DilosFile.Encoding.GetBytes("€")[0]);
    }
}
