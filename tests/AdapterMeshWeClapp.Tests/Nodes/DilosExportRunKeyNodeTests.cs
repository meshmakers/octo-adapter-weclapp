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

    // The K1 probe addresses the two values as $.meta.exportKind and $.meta.exportDay. If the
    // write mode nested the object differently, both would resolve to null, the GetOrCreate
    // filters would match on nothing and the gate would sit permanently closed - green, silent
    // and undelivered.
    [Fact]
    public async Task TheWrittenObject_IsReadableAtTheSubPathsTheProbeUses()
    {
        var config = Configure();
        var (data, node) = Context(config);
        var sut = new DilosExportRunKeyNode(_next,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)));

        await sut.ProcessObjectAsync(data, node);

        Assert.Equal("AS", data.Get<string>("$.meta.exportKind"));
        Assert.Equal("2026-07-23", data.Get<string>("$.meta.exportDay"));
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
