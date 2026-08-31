using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class DilosExportRunKeyNodeTests
{
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();

    private static (DataContextImpl Data, INodeContext Node) Context(DilosExportRunKeyNodeConfiguration config)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        return (dataContext, rootContext.RegisterChildNode("DilosExportRunKey", 0, config, dataContext));
    }

    private static DilosExportRunKeyNodeConfiguration Configure() =>
        new() { ExportKind = "AS", TargetPath = "$.meta" };

    [Fact]
    public async Task WritesTheExportKindAndTheViennaCalendarDay()
    {
        var config = Configure();
        var (data, node) = Context(config);
        // 10:00 UTC = 12:00 Vienna (CEST), same calendar day either way.
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        var meta = data.Get<JsonObject>("$.meta");
        Assert.NotNull(meta);
        Assert.Equal("AS", meta["exportKind"]!.ToString());
        Assert.Equal("2026-07-23", meta["exportDay"]!.ToString());
        A.CallTo(() => _next(data, node)).MustHaveHappenedOnceExactly();
    }

    // The reason this node exists at all. DateTime@1 has no time zone, so a standard-node
    // composition would answer "2026-07-23" here and key the day marker to the wrong day for
    // the first two hours of every Vienna day.
    [Fact]
    public async Task JustAfterViennaMidnightInSummer_UsesTheViennaDay_NotTheUtcDay()
    {
        var config = Configure();
        var (data, node) = Context(config);
        // 22:30 UTC on the 23rd = 00:30 Vienna on the 24th (CEST, UTC+2).
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 22, 30, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        Assert.Equal("2026-07-24", data.Get<JsonObject>("$.meta")!["exportDay"]!.ToString());
    }

    [Fact]
    public async Task JustAfterViennaMidnightInWinter_UsesTheViennaDay()
    {
        // 23:30 UTC on the 15th = 00:30 Vienna on the 16th (CET, UTC+1). A fixed hour offset
        // would be wrong for one of these two tests whichever offset it picked.
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 23, 30, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        Assert.Equal("2026-01-16", data.Get<JsonObject>("$.meta")!["exportDay"]!.ToString());
    }

    [Fact]
    public async Task AtTheSpringDstSwitch_TheDayIsStillTheViennaDay()
    {
        // 01:00 UTC on 29 March 2026 is the instant Vienna jumps from 02:00 CET to 03:00 CEST.
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        Assert.Equal("2026-03-29", data.Get<JsonObject>("$.meta")!["exportDay"]!.ToString());
    }

    // The K1 probe addresses two of the values as $.meta.exportKind and $.meta.exportDay, and the
    // delivery reads the third as $.meta.fileName. If the write mode nested the object
    // differently, all three would resolve to null: the GetOrCreate filters would match on
    // nothing and the gate would sit permanently closed - green, silent and undelivered - while
    // the upload would have no name at all.
    [Fact]
    public async Task TheWrittenObject_IsReadableAtTheSubPathsTheProbeAndTheDeliveryUse()
    {
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        Assert.Equal("AS", data.Get<string>("$.meta.exportKind"));
        Assert.Equal("2026-07-23", data.Get<string>("$.meta.exportDay"));
        Assert.Equal("AS20260723120000.txt", data.Get<string>("$.meta.fileName"));
    }

    // ---- the delivery file name comes from the SAME clock read as the day (decision D3) ----

    // Until now the day was stamped here and the file name at render time, after two paged HTTP
    // fetches. A run crossing Vienna midnight between the two reads therefore delivered a file
    // named for day N+1 under the marker of day N, and since no marker existed for N+1 the next
    // tick delivered that day again. One read makes the divergence unrepresentable.
    [Fact]
    public async Task WritesTheDeliveryFileNameFromTheSameClockRead()
    {
        var config = Configure();
        var (data, node) = Context(config);
        // 20:30:15 UTC = 22:30:15 Vienna (CEST) - late evening, still the same calendar day.
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 20, 30, 15, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        var meta = data.Get<JsonObject>("$.meta");
        Assert.NotNull(meta);
        Assert.Equal("2026-08-28", meta["exportDay"]!.ToString());
        Assert.Equal("AS20260828223015.txt", meta["fileName"]!.ToString());
    }

    // The coupling itself, one second either side of Vienna midnight: BOTH values move, and they
    // move together. Two literals that happen to agree would not show that - this asserts the
    // name's date part against the day the marker is keyed on, which is the invariant a second
    // clock read breaks.
    //
    // The coupling rests on TWO independent conversions - the day is converted here, the name
    // inside DilosFile.DeliveryFileName - so the rows have to cover both offsets Vienna has. All
    // the summer rows agree with a fixed +02:00, which is why the winter row is here: it is the
    // one a "simplification" of either conversion to a constant offset gets wrong, and it gets it
    // wrong by naming the file for the NEXT day while the marker still keys this one - the exact
    // split D3 closed.
    [Theory]
    [InlineData("2026-08-28T21:59:59Z", "2026-08-28", "AS20260828235959.txt")] // 23:59:59 Vienna, CEST
    [InlineData("2026-08-28T22:00:00Z", "2026-08-29", "AS20260829000000.txt")] // 00:00:00 Vienna, CEST
    [InlineData("2026-01-15T22:30:00Z", "2026-01-15", "AS20260115233000.txt")] // 23:30:00 Vienna, CET
    public async Task AcrossViennaMidnight_TheDayAndTheFileNameMoveTogether(
        string utcNow, string expectedDay, string expectedFileName)
    {
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next, new FixedTimeProvider(
            DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));

        await sut.ProcessObjectAsync(data, node);

        var meta = data.Get<JsonObject>("$.meta");
        Assert.NotNull(meta);
        var day = meta["exportDay"]!.ToString();
        var fileName = meta["fileName"]!.ToString();
        Assert.Equal(expectedDay, day);
        Assert.Equal(expectedFileName, fileName);
        Assert.StartsWith("AS" + day.Replace("-", ""), fileName, StringComparison.Ordinal);
    }

    // The existence proof, and the only test here that a TWO-read implementation fails: a fixed
    // clock answers both reads identically, so the coupling above holds for the old arrangement
    // too. This clock moves one second between reads, across Vienna midnight - exactly the tick
    // that produced a day-N marker beside a day-N+1 file name, after which the next tick delivered
    // that day a second time because no marker existed for N+1 yet.
    [Fact]
    public async Task AClockThatMovesBetweenReads_CannotSplitTheDayFromTheFileName()
    {
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next, new SteppingTimeProvider(
            new DateTimeOffset(2026, 8, 28, 21, 59, 59, TimeSpan.Zero),  // 23:59:59 Vienna, day N
            new DateTimeOffset(2026, 8, 28, 22, 0, 0, TimeSpan.Zero)));  // 00:00:00 Vienna, day N+1

        await sut.ProcessObjectAsync(data, node);

        var meta = data.Get<JsonObject>("$.meta");
        Assert.NotNull(meta);
        Assert.Equal("2026-08-28", meta["exportDay"]!.ToString());
        Assert.Equal("AS20260828235959.txt", meta["fileName"]!.ToString());
    }

    /// <summary>A clock that advances on every read, so a second read is observable. Used by the
    /// test above and by nothing else: every other clock-dependent assertion wants the fixed
    /// provider.</summary>
    private sealed class SteppingTimeProvider(params DateTimeOffset[] reads) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow() => reads[Math.Min(_index++, reads.Length - 1)];
    }

    // The name is machine-generated, so this is theoretical - but it is also free, and the render
    // node it moves from carried the same guard because a DILOS file name is a bare name and the
    // delivery node resolves one carrying path segments to its last segment instead of refusing it.
    [Theory]
    [InlineData("A/S")]
    [InlineData("A\\S")]
    [InlineData("..")]
    public async Task ExportKindThatWouldPoisonTheFileName_FailsBeforeWritingAnything(string exportKind)
    {
        var config = new DilosExportRunKeyNodeConfiguration { ExportKind = exportKind, TargetPath = "$.meta" };
        var (data, node) = Context(config);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new DilosExportRunKeyNode(_next).ProcessObjectAsync(data, node));

        Assert.False(data.Exists("$.meta"));
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankExportKind_FailsBeforeWritingAnything(string? exportKind)
    {
        var config = new DilosExportRunKeyNodeConfiguration { ExportKind = exportKind!, TargetPath = "$.meta" };
        var (data, node) = Context(config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new DilosExportRunKeyNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("ExportKind", ex.Message);
        Assert.False(data.Exists("$.meta"));
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BlankTargetPath_FailsBeforeWritingAnything(string? targetPath)
    {
        var config = new DilosExportRunKeyNodeConfiguration { ExportKind = "AS", TargetPath = targetPath! };
        var (data, node) = Context(config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new DilosExportRunKeyNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("TargetPath", ex.Message);
    }
}
