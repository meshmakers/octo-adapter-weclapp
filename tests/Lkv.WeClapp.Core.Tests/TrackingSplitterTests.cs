using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class TrackingSplitterTests
{
    // The four URL shapes and the bare-number case below are verbatim golden
    // values (AR*.TXT test files supplied by LKV).

    [Fact]
    public void Split_DpdUrl_DedupesToSingleTokenWithSuffix()
    {
        var info = TrackingSplitter.Split(
            "http://www.mydpd.at/?f=parcel.load&p=06255052795778-Z,06255052795778-Z");

        Assert.Equal(["06255052795778-Z"], info.Numbers);
        Assert.Equal("http://www.mydpd.at/?f=parcel.load&p=06255052795778-Z,06255052795778-Z", info.Url);
    }

    [Fact]
    public void Split_DhlUrl_DedupesToSingleToken()
    {
        var info = TrackingSplitter.Split(
            "http://nolp.dhl.de/nextt-online-public/set_identcodes.do?lang=de&idc=00340434694230620796,00340434694230620796");

        Assert.Equal(["00340434694230620796"], info.Numbers);
    }

    [Fact]
    public void Split_PostAtUrl_DedupesToSingleToken()
    {
        var info = TrackingSplitter.Split(
            "http://www.post.at/tnt_query.php?pnum1=1013408501786603907562,1013408501786603907562");

        Assert.Equal(["1013408501786603907562"], info.Numbers);
    }

    [Fact]
    public void Split_UpsUrl_DedupesToSingleToken()
    {
        var info = TrackingSplitter.Split(
            "https://wwwapps.ups.com/WebTracking/track?track=yes&trackNums=1Z534V096846393410,1Z534V096846393410");

        Assert.Equal(["1Z534V096846393410"], info.Numbers);
    }

    [Fact]
    public void Split_BareNumberWithoutUrl_ReturnsTokenWithoutUrl()
    {
        // Golden carrier code 9: plain number, no URL, no comma.
        var info = TrackingSplitter.Split("1013408501850970172035");

        Assert.Equal(["1013408501850970172035"], info.Numbers);
        Assert.Null(info.Url);
    }

    [Fact]
    public void Split_GenuinelyDistinctNumbers_KeepsAllInOrder()
    {
        // Never seen in goldens, but the splitter must not lose real data.
        var info = TrackingSplitter.Split("http://example.test/track?p=AAA,BBB,AAA");

        Assert.Equal(["AAA", "BBB"], info.Numbers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_EmptyOrWhitespace_ReturnsNoTokens(string value)
    {
        var info = TrackingSplitter.Split(value);

        Assert.Empty(info.Numbers);
        Assert.Null(info.Url);
    }

    [Fact]
    public void Split_AllGoldenParcels_YieldExactlyOneTokenEach()
    {
        var arFiles = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "AR*.TXT");
        Assert.Equal(5, arFiles.Length);

        var parcels = arFiles
            .Select(File.ReadAllText)
            .SelectMany(DilosArParser.Parse)
            .SelectMany(s => s.Parcels)
            .ToList();
        Assert.Equal(103, parcels.Count);

        foreach (var parcel in parcels)
        {
            var info = TrackingSplitter.Split(parcel.TrackingNumber);

            Assert.Single(info.Numbers);
            if (info.Url is not null)
            {
                Assert.Contains(info.Numbers[0], info.Url);
            }
        }
    }
}
