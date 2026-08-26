using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// DilosFileGate@1 — the state gate between the product's <c>SftpList@1</c> listing and the
/// per-file <c>ForEach@1</c>. Runs against a REAL <see cref="DataContextImpl" /> rather than a
/// faked data context: the elements it works on cross a JSON boundary on their way in and out,
/// and a fake would let the node read and write shapes the real context never produces.
/// </summary>
public class DilosFileGateNodeTests
{
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly DilosFileFetchState _state = new();
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpFileSystemFactory _sftpFactory = A.Fake<ISftpFileSystemFactory>();
    private readonly ISftpFileSystem _sftp = A.Fake<ISftpFileSystem>();

    private static readonly SftpConnectionSettings PasswordSettings = new()
    {
        Host = "sftp.lkv.example",
        Username = "weclapp",
        Password = "secret",
    };

    private DilosFileGateNode CreateSut()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp")).Returns(PasswordSettings);
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).Returns(_sftp);
        return new DilosFileGateNode(_next, A.Fake<ILogger<DilosFileGateNode>>(), _etlContext, _sftpFactory, _state);
    }

    private DilosFileGateNodeConfiguration Configure(bool deleteAfterSuccess = false, string path = "$.files")
    {
        var config = new DilosFileGateNodeConfiguration
        {
            DeleteAfterSuccess = deleteAfterSuccess,
            Path = path,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosFileGateNodeConfiguration>()).Returns(config);
        return config;
    }

    /// <summary>One element in the shape <c>SftpList@1</c> emits: metadata plus the nested
    /// <c>source</c> object naming the listing it came from.</summary>
    private static string Listed(string name, long length = 100,
        string lastWriteTimeUtc = "2026-08-20T10:00:00.0000000Z", string filePattern = "AR*TXT") =>
        $$$"""
            {"name":"{{{name}}}","fullPath":"/{{{name}}}","length":{{{length}}},
             "lastWriteTimeUtc":"{{{lastWriteTimeUtc}}}",
             "source":{"serverConfiguration":"LkvSftp","remoteDirectory":"/",
                       "filePattern":"{{{filePattern}}}"}}
            """;

    private static DataContextImpl ListingOf(params string[] elements) =>
        new(JsonDocument.Parse("{\"files\":[" + string.Join(",", elements) + "]}"));

    /// <summary>The scoped key the gate derives from ONE listed element — composed from the same
    /// <see cref="DilosFileFetchCore" /> helpers the node uses, so these tests pin the node's
    /// wiring rather than re-declaring the key format.</summary>
    private static string ScopedKey(string name, long length = 100,
        string lastWriteTimeUtc = "2026-08-20T10:00:00.0000000Z", string filePattern = "AR*TXT") =>
        DilosFileFetchCore.ScopePrefix("LkvSftp", "/", filePattern) +
        DilosFileFetchCore.FileKey(name, length, lastWriteTimeUtc);

    private static JsonArray Files(IDataContext dataContext) =>
        dataContext.Get<JsonArray>("$.files") ?? throw new InvalidOperationException("no files array");

    [Fact]
    public async Task PassesListedElementThrough_StampingKeyModeAndServer()
    {
        Configure(deleteAfterSuccess: false);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        var file = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal(ScopedKey("AR1.TXT"), file["key"]!.GetValue<string>());
        Assert.False(file["deleteAfterSuccess"]!.GetValue<bool>());
        Assert.Equal("LkvSftp", file["serverConfiguration"]!.GetValue<string>());
        // The listing's own fields survive — the per-file chain reads them downstream.
        Assert.Equal("AR1.TXT", file["name"]!.GetValue<string>());
        Assert.Equal("/AR1.TXT", file["fullPath"]!.GetValue<string>());
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task KeepMode_DropsElementAlreadyConfirmedKept()
    {
        // Keep mode leaves the file on the server, so without this the same file would be
        // downloaded and written again on every tick for as long as it lies there.
        Configure(deleteAfterSuccess: false);
        using var dataContext = ListingOf(Listed("AR1.TXT"), Listed("AR2.TXT", length: 200));
        _state.MarkKeptOnServer(ScopedKey("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        var survivor = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal("AR2.TXT", survivor["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteMode_IgnoresAKeepMarkLeftOverFromKeepMode()
    {
        // After a mode flip the singleton still holds the other mode's marks. A keep mark must
        // never suppress an emission in delete mode: the file would then lie on the LKV server
        // untouched, listed on every tick and processed on none.
        Configure(deleteAfterSuccess: true);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        _state.MarkKeptOnServer(ScopedKey("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        var survivor = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal("AR1.TXT", survivor["name"]!.GetValue<string>());
        Assert.True(survivor["deleteAfterSuccess"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DeleteMode_RetriesAPendingDelete_WithoutEmittingTheFileAgain()
    {
        // DilosFileConfirm@1 processed this file in an earlier tick and only its delete failed.
        // Re-emitting it would write the same file to WeClapp a second time; the gate owes the
        // server the delete and nothing else.
        Configure(deleteAfterSuccess: true);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        _state.MarkPendingDelete(ScopedKey("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustHaveHappenedOnceExactly();
        Assert.False(_state.HasPendingDelete(ScopedKey("AR1.TXT")));
        Assert.Empty(Files(dataContext));
    }

    [Fact]
    public async Task KeepMode_NeverDeletesOnAStalePendingDeleteMark()
    {
        // The mirror image of the keep-mark rule: a pending delete left over from delete mode
        // must not remove an LKV file while the pipeline is configured to keep files.
        Configure(deleteAfterSuccess: false);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        _state.MarkPendingDelete(ScopedKey("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        var survivor = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal("AR1.TXT", survivor["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task PendingDeleteRetryFailure_KeepsTheKeyPendingAndLetsTheOtherFilesThrough()
    {
        // One remote entry refusing to be deleted is a property of that entry, not of the tick:
        // the retry stays owed and the files behind it still reach the per-file chain.
        Configure(deleteAfterSuccess: true);
        using var dataContext = ListingOf(Listed("AR1.TXT"), Listed("AR2.TXT", length: 200));
        _state.MarkPendingDelete(ScopedKey("AR1.TXT"));
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).Throws(new IOException("permission denied"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.True(_state.HasPendingDelete(ScopedKey("AR1.TXT")));
        var survivor = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal("AR2.TXT", survivor["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ForgetsKeysOfFilesThatVanishedFromItsOwnScope()
    {
        // Without pruning the singleton grows for the lifetime of the pod, one entry per file
        // that ever passed through - and the entries are unreachable once the file is gone.
        Configure(deleteAfterSuccess: false);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        _state.MarkKeptOnServer(ScopedKey("AR_gone.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.False(_state.WasKeptOnServer(ScopedKey("AR_gone.TXT")));
    }

    [Fact]
    public async Task PruningNeverReachesAnotherPipelinesScope()
    {
        // The ar and be pipelines resolve the SAME DilosFileFetchState singleton. An ar tick
        // lists no BE file, so an unscoped prune would drop every be mark and make be re-process
        // files it had already confirmed - on be's next tick, with nothing in ar's log about it.
        Configure(deleteAfterSuccess: false);
        var beKey = ScopedKey("BE_20240205035403463.txt", filePattern: "BE*txt");
        _state.MarkKeptOnServer(beKey);
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer(beKey));
    }

    [Fact]
    public async Task DryRun_EmitsFilesButNeverDeletesPrunesOrClearsState()
    {
        // A dry-run probe must leave no trace on the server or in the cross-tick memory, while
        // the chain behind it still sees data to run against - the contract the write nodes
        // already keep.
        Configure(deleteAfterSuccess: true);
        using var dataContext = ListingOf(Listed("AR1.TXT"), Listed("AR2.TXT", length: 200));
        _state.MarkPendingDelete(ScopedKey("AR1.TXT"));
        _state.MarkPendingDelete(ScopedKey("AR_gone.TXT"));
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        Assert.True(_state.HasPendingDelete(ScopedKey("AR1.TXT")));
        Assert.True(_state.HasPendingDelete(ScopedKey("AR_gone.TXT"))); // not pruned either
        var survivor = Assert.Single(Files(dataContext))!.AsObject();
        Assert.Equal("AR2.TXT", survivor["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusesAnElementWithoutTheListingsSourceStamp()
    {
        // The gate derives its scope from the listing the element came from. An element without
        // that object is not a listing element: the path is wired to the wrong array, and
        // guessing a scope would key the state to something that means nothing.
        Configure();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(
            """
            {"files":[{"name":"AR1.TXT","fullPath":"/AR1.TXT","length":100,
                       "lastWriteTimeUtc":"2026-08-20T10:00:00.0000000Z"}]}
            """));
        var sut = CreateSut();

        var error = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.Contains("source.serverConfiguration", error.Message);
    }

    [Fact]
    public async Task RefusesAnElementWithoutTheTimestampTheKeyIsBuiltFrom()
    {
        // Silently keying without it would give every listing of the same file the same key as
        // every other file of that name and size - a rewritten file would count as processed.
        Configure();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(
            """
            {"files":[{"name":"AR1.TXT","fullPath":"/AR1.TXT","length":100,
                       "source":{"serverConfiguration":"LkvSftp","remoteDirectory":"/",
                                 "filePattern":"AR*TXT"}}]}
            """));
        var sut = CreateSut();

        var error = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.Contains("lastWriteTimeUtc", error.Message);
    }

    [Fact]
    public async Task TwoListingsOfAnUnchangedFile_ProduceTheIdenticalKey()
    {
        // The file identity crosses a JSON boundary now: SftpList@1 renders the modification
        // time as text, and the gate keys the cross-tick state on that text. If the rendering
        // shifted between two listings of the SAME unchanged file, every tick would mint a new
        // key, no keep mark would ever match, and the file would be delivered again on every
        // tick - a failure that looks like "keep mode does not work" and appears in no log.
        // Driven through the REAL SftpList@1, because the rendering is that node's decision.
        var entry = new SftpEntry("AR1.TXT", "/AR1.TXT", false, 100, new DateTime(2026, 8, 20, 10, 14, 32, 987,
            DateTimeKind.Utc));

        var first = await ListAndGate(entry);
        var second = await ListAndGate(entry);

        Assert.Equal(first, second);
    }

    /// <summary>Runs the product's real <c>SftpList@1</c> and this gate over one remote entry,
    /// the way the pipeline runs them, and returns the key the gate stamped.</summary>
    private async Task<string> ListAndGate(SftpEntry entry)
    {
        var serverSettings = new SftpServerSettings
        {
            Host = "sftp.lkv.example",
            Username = "weclapp",
            Password = "secret",
        };
        var session = A.Fake<ISftpSession>();
        A.CallTo(() => session.List("/")).Returns(new[] { entry });
        var sessionFactory = A.Fake<ISftpSessionFactory>();
        A.CallTo(() => sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).Returns(session);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>("LkvSftp")).Returns(serverSettings);
        A.CallTo(() => _nodeContext.GetNodeConfiguration<SftpListNodeConfiguration>()).Returns(
            new SftpListNodeConfiguration
            {
                ServerConfiguration = "LkvSftp",
                RemoteDirectory = "/",
                FilePattern = "AR*TXT",
                TargetPath = "$.files",
            });
        Configure(deleteAfterSuccess: false);

        using var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        var list = new SftpListNode(_next, _etlContext, sessionFactory);
        var gate = CreateSut();

        await list.ProcessObjectAsync(dataContext, _nodeContext);
        await gate.ProcessObjectAsync(dataContext, _nodeContext);

        return Assert.Single(Files(dataContext))!.AsObject()["key"]!.GetValue<string>();
    }

    [Fact]
    public async Task KeysOnTheListingsOwnTimestampText_NotOnAReformattedValue()
    {
        // The gate must carry the listing's rendering through untouched. Parsing it and
        // formatting it again would tie the identity to THIS side's format choice: the same
        // instant would key differently depending on which side rendered it, and a file already
        // processed would look new. The timestamp here has no fractional digits, so a re-format
        // is visible in the key.
        Configure();
        using var dataContext = ListingOf(Listed("AR1.TXT", lastWriteTimeUtc: "2026-08-20T10:00:00Z"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        var key = Assert.Single(Files(dataContext))!.AsObject()["key"]!.GetValue<string>();
        Assert.EndsWith("|2026-08-20T10:00:00Z", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyListing_WritesAnEmptyArrayAndRunsOn()
    {
        // The ForEach@1 behind the gate aborts with PathMustBeArray when its iteration path
        // holds no array, so an empty tick has to leave an empty array behind, not nothing.
        Configure();
        using var dataContext = ListingOf();
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.Empty(Files(dataContext));
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RefusesToGateAPathThatHoldsNothing()
    {
        // Both this path and the listing node's targetPath are configurable, in a tenant-side
        // definition an operator can edit. Pointed at a path the listing never wrote, the gate
        // reads nothing and writes an empty array - and the run stays green forever while the
        // DILOS files pile up unprocessed on the LKV server. The empty array it writes is what
        // hides the failure: without it the ForEach behind the gate would abort and say so.
        Configure(path: "$.somewhereElse");
        using var dataContext = ListingOf(Listed("AR1.TXT"));
        var sut = CreateSut();

        var error = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.Contains("$.somewhereElse", error.Message);
    }

    [Fact]
    public async Task EmptyListing_LeavesEarlierMarksInPlace()
    {
        // Documents an accepted difference from the node this replaces. The gate derives the
        // scopes it prunes from the elements it is handed, so an empty listing prunes nothing,
        // while the old node pruned its own configured scope unconditionally. In keep mode a
        // file that disappears and later returns byte-identical with its modification time
        // preserved therefore keys the same and is dropped as already processed, until the pod
        // restarts. Reading the scope off the elements is what removes the duplicated
        // server/directory/pattern triple from this node, so this is that trade, not an
        // oversight - and in delete mode, where files do not linger, it cannot arise.
        Configure(deleteAfterSuccess: false);
        _state.MarkKeptOnServer(ScopedKey("AR_gone.TXT"));
        using var dataContext = ListingOf();
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer(ScopedKey("AR_gone.TXT")));
    }
}
