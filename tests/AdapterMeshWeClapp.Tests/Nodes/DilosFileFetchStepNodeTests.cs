using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// DilosFileFetchStep@1 — the step-node counterpart of DilosFileFetchTriggerNode for the
/// cron-trigger redesign (AB#4228/G2): lists, filters and downloads into <c>$.files</c>
/// instead of calling <c>ITriggerContext.ExecuteAsync</c> per file. Fakes mirror
/// DilosFileFetchTriggerNodeTests.cs (SFTP seam); the SFTP settings seam itself mirrors
/// DilosSftpWriteNodeTests.cs (IMeshEtlContext, the transform-node access pattern).
/// </summary>
public class DilosFileFetchStepNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpFileSystemFactory _sftpFactory = A.Fake<ISftpFileSystemFactory>();
    private readonly ISftpFileSystem _sftp = A.Fake<ISftpFileSystem>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly DilosFileFetchState _state = new();
    private JsonArray? _capturedFiles;

    private static readonly SftpConnectionSettings PasswordSettings = new()
    {
        Host = "sftp.lkv.example",
        Username = "weclapp",
        Password = "secret",
    };

    private DilosFileFetchStepNode CreateSut()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp")).Returns(PasswordSettings);
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).Returns(_sftp);
        A.CallTo(() => _dataContext.Set("$.files", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => _capturedFiles = (JsonArray?)call.Arguments[1]);
        return new DilosFileFetchStepNode(_next, A.Fake<ILogger<DilosFileFetchStepNode>>(), _etlContext,
            _sftpFactory, _state);
    }

    private DilosFileFetchStepNodeConfiguration Configure(string pattern = "AR*TXT",
        string serverConfiguration = "LkvSftp", int minFileAgeSeconds = 60, bool deleteAfterSuccess = true)
    {
        var config = new DilosFileFetchStepNodeConfiguration
        {
            ServerConfiguration = serverConfiguration,
            FilePattern = pattern,
            MinFileAgeSeconds = minFileAgeSeconds,
            DeleteAfterSuccess = deleteAfterSuccess,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosFileFetchStepNodeConfiguration>()).Returns(config);
        return config;
    }

    private static SftpFileEntry RemoteFile(string name, int ageMinutes = 10, long length = 100) =>
        new(name, "/" + name, false, DateTime.UtcNow.AddMinutes(-ageMinutes), length);

    private void ListingReturns(params SftpFileEntry[] entries) =>
        A.CallTo(() => _sftp.ListFiles("/")).Returns(entries);

    /// <summary>The namespaced key <c>DilosFileFetchStepNode</c> actually reads/writes/emits:
    /// a scope prefix built from the step's OWN config, followed by the file's bare
    /// <see cref="DilosFileFetchCore.FileKey"/> as a suffix — composed from the SAME
    /// <see cref="DilosFileFetchCore"/> helpers the node uses, so these tests pin the node's
    /// wiring (scope + key composition), not a re-declared key format.</summary>
    private static string ScopedKey(DilosFileFetchStepNodeConfiguration config, SftpFileEntry file) =>
        DilosFileFetchCore.ScopePrefix(config) + DilosFileFetchCore.FileKey(file);

    [Fact]
    public async Task EmitsFiles_NameOrdered_RespectingMinFileAge_WithKeyAndFullPath()
    {
        var config = Configure("AR*TXT", minFileAgeSeconds: 60);
        var young = new SftpFileEntry("AR_young.TXT", "/AR_young.TXT", false, DateTime.UtcNow.AddSeconds(-5), 50);
        var first = RemoteFile("AR20240205143134947.TXT", ageMinutes: 20, length: 111);
        var second = RemoteFile("AR00006946.TXT", ageMinutes: 30, length: 222);
        // Listing deliberately unsorted.
        ListingReturns(first, young, second);
        A.CallTo(() => _sftp.DownloadText("/AR00006946.TXT")).Returns("K|content-1");
        A.CallTo(() => _sftp.DownloadText("/AR20240205143134947.TXT")).Returns("K|content-2");
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(_capturedFiles);
        Assert.Equal(2, _capturedFiles!.Count); // young file skipped
        Assert.Equal("AR00006946.TXT", _capturedFiles[0]!["fileName"]!.ToString()); // Ordinal name order
        Assert.Equal("K|content-1", _capturedFiles[0]!["content"]!.ToString());
        Assert.Equal("/AR00006946.TXT", _capturedFiles[0]!["fullPath"]!.ToString());
        Assert.Equal(ScopedKey(config, second), _capturedFiles[0]!["key"]!.ToString());
        Assert.Equal(second.LastWriteTimeUtc, _capturedFiles[0]!["lastWriteTimeUtc"]!.GetValue<DateTime>());
        Assert.Equal("AR20240205143134947.TXT", _capturedFiles[1]!["fileName"]!.ToString());
        Assert.Equal(ScopedKey(config, first), _capturedFiles[1]!["key"]!.ToString());
        A.CallTo(() => _sftp.DownloadText("/AR_young.TXT")).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NoFiles_SeedsEmptyFilesArray()
    {
        Configure("AR*TXT");
        ListingReturns();
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(_capturedFiles);
        Assert.Empty(_capturedFiles!);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PendingDeleteRetry_DeletesDuringListing_WithoutEmitting()
    {
        var config = Configure("AR*TXT", deleteAfterSuccess: true);
        var file = RemoteFile("AR1.TXT", ageMinutes: 20, length: 100);
        ListingReturns(file);
        _state.MarkPendingDelete(ScopedKey(config, file)); // DilosFileConfirm@1 processed it, delete failed then
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _sftp.DownloadText(A<string>._)).MustNotHaveHappened(); // never re-downloaded
        Assert.False(_state.HasPendingDelete(ScopedKey(config, file))); // cleared after the successful retry
        Assert.NotNull(_capturedFiles);
        Assert.Empty(_capturedFiles!); // not (re-)emitted
    }

    [Fact]
    public async Task PendingDeleteRetry_FailureLeavesKeyPendingAndDoesNotBlockOtherFiles()
    {
        // Retry-delete failure isolation: a failed retry-delete must stay retryable (not lost)
        // and must not stop other files in the same listing from being processed — mirrors
        // DilosFileFetchTriggerNode's own delete-retry isolation in FetchOnceAsync.
        var config = Configure("AR*TXT", deleteAfterSuccess: true);
        var pending = RemoteFile("AR1.TXT", ageMinutes: 20, length: 100);
        var ok = RemoteFile("AR2.TXT", ageMinutes: 20, length: 200);
        ListingReturns(pending, ok);
        _state.MarkPendingDelete(ScopedKey(config, pending));
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).Throws(new IOException("permission denied"));
        A.CallTo(() => _sftp.DownloadText("/AR2.TXT")).Returns("content");
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext); // must not throw

        Assert.True(_state.HasPendingDelete(ScopedKey(config, pending))); // still pending, retried next tick
        Assert.NotNull(_capturedFiles);
        var only = Assert.Single(_capturedFiles!);
        Assert.Equal("AR2.TXT", only!["fileName"]!.ToString()); // the other file still got listed
    }

    [Fact]
    public async Task KeepMode_SecondRun_SkipsAlreadyEmittedFiles()
    {
        var config = Configure("AR*TXT", deleteAfterSuccess: false);
        var file = RemoteFile("AR1.TXT", ageMinutes: 20);
        ListingReturns(file);
        _state.MarkKeptOnServer(ScopedKey(config, file)); // DilosFileConfirm@1 already confirmed it kept
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(_capturedFiles);
        Assert.Empty(_capturedFiles!); // skipped, not re-emitted
        A.CallTo(() => _sftp.DownloadText(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task VanishedKeptFile_IsForgottenByPruneScopeTo()
    {
        var config = Configure("AR*TXT", deleteAfterSuccess: false);
        var vanished = RemoteFile("AR_gone.TXT", ageMinutes: 20);
        _state.MarkKeptOnServer(ScopedKey(config, vanished)); // kept from an earlier tick
        ListingReturns(); // the file is no longer on the server this tick
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.False(_state.WasKeptOnServer(ScopedKey(config, vanished)));
    }

    [Fact]
    public async Task VanishedPendingDeleteFile_IsForgottenByPruneScopeTo()
    {
        var config = Configure("AR*TXT", deleteAfterSuccess: true);
        var vanished = RemoteFile("AR_gone.TXT", ageMinutes: 20);
        _state.MarkPendingDelete(ScopedKey(config, vanished)); // pending from an earlier tick
        ListingReturns(); // the file is no longer on the server this tick
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.False(_state.HasPendingDelete(ScopedKey(config, vanished)));
    }

    [Fact]
    public async Task DryRun_StillEmitsFiles_ButNeverDeletesPrunesOrClearsState()
    {
        // A dry-run execution (manual FromExecutePipelineCommand@1 probe) must leave no trace:
        // no remote deletes and no cross-tick state writes — only the read-and-emit surface
        // runs, mirroring the dry-run contract of the write nodes (DilosSftpWriteNode.cs:84).
        var config = Configure("AR*TXT", deleteAfterSuccess: true);
        var pending = RemoteFile("AR1.TXT", ageMinutes: 20, length: 100);
        var fresh = RemoteFile("AR2.TXT", ageMinutes: 20, length: 200);
        var vanished = RemoteFile("AR_gone.TXT", ageMinutes: 20);
        ListingReturns(pending, fresh);
        _state.MarkPendingDelete(ScopedKey(config, pending)); // real earlier tick: delete failed
        _state.MarkPendingDelete(ScopedKey(config, vanished)); // real earlier tick: file now gone
        A.CallTo(() => _sftp.DownloadText("/AR2.TXT")).Returns("content");
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened(); // no retry-delete
        Assert.True(_state.HasPendingDelete(ScopedKey(config, pending))); // key untouched
        Assert.True(_state.HasPendingDelete(ScopedKey(config, vanished))); // no pruning either
        Assert.NotNull(_capturedFiles);
        var only = Assert.Single(_capturedFiles!); // fresh file still emitted for probing
        Assert.Equal("AR2.TXT", only!["fileName"]!.ToString());
    }

    [Fact]
    public async Task SharedSingleton_OwnScopeTick_NeverPrunesOrSkipsAnotherScopesKeys()
    {
        // Guards against cross-scope pruning of the shared singleton: TWO pipelines' step nodes
        // (ar, be) resolve the SAME DilosFileFetchState DI singleton (Program.cs) — a tick
        // listing only its own filePattern must never prune or skip another scope's keys.
        // Without scoping, an ar-only prune call would intersect BOTH global sets,
        // discarding every be key an ar tick never listed — a be file already confirmed "kept on
        // server" would then be silently re-emitted and re-executed on be's OWN next tick (its
        // kept-on-server mark having been wiped by the unrelated ar tick in between).
        var beConfig = Configure("BE*txt", deleteAfterSuccess: false);
        var beFile = RemoteFile("BE_20240205035403463.txt", ageMinutes: 20);
        _state.MarkKeptOnServer(ScopedKey(beConfig, beFile)); // be confirmed this kept, in an earlier tick

        // An unrelated ar tick now runs against the SAME _state singleton and lists only AR files.
        Configure("AR*TXT", deleteAfterSuccess: false);
        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 20));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // be's kept key must survive an ar tick that never mentioned it — and ar itself must not
        // have been affected by be's pre-existing entry either.
        Assert.True(_state.WasKeptOnServer(ScopedKey(beConfig, beFile)));
        Assert.NotNull(_capturedFiles);
        var only = Assert.Single(_capturedFiles!);
        Assert.Equal("AR1.TXT", only!["fileName"]!.ToString());
    }
}

/// <summary>
/// DilosFileConfirm@1 — the LAST child inside the per-file <c>ForEach@1</c> that
/// DilosFileFetchStep@1 feeds. Fakes mirror DilosSftpWriteNodeTests.cs (same
/// IMeshEtlContext/ISftpFileSystemFactory seam, DilosSftpWriteNode.cs:82).
/// </summary>
public class DilosFileConfirmNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpFileSystemFactory _sftpFactory = A.Fake<ISftpFileSystemFactory>();
    private readonly ISftpFileSystem _sftp = A.Fake<ISftpFileSystem>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly DilosFileFetchState _state = new();

    private static readonly SftpConnectionSettings PasswordSettings = new()
    {
        Host = "sftp.lkv.example",
        Username = "weclapp",
        Password = "secret",
    };

    private DilosFileConfirmNode CreateSut()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp")).Returns(PasswordSettings);
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).Returns(_sftp);
        return new DilosFileConfirmNode(_next, _etlContext, _sftpFactory, _state);
    }

    private DilosFileConfirmNodeConfiguration Configure(bool deleteAfterSuccess, string path = "$.current",
        string serverConfiguration = "LkvSftp")
    {
        var config = new DilosFileConfirmNodeConfiguration
        {
            ServerConfiguration = serverConfiguration,
            DeleteAfterSuccess = deleteAfterSuccess,
            Path = path,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosFileConfirmNodeConfiguration>()).Returns(config);
        return config;
    }

    private void SetCurrentElement(string key, string fullPath = "/AR1.TXT", string fileName = "AR1.TXT")
    {
        A.CallTo(() => _dataContext.Get<string>("$.current.key")).Returns(key);
        A.CallTo(() => _dataContext.Get<string>("$.current.fullPath")).Returns(fullPath);
        A.CallTo(() => _dataContext.Get<string>("$.current.fileName")).Returns(fileName);
    }

    [Fact]
    public async Task ConfirmNode_ReadsSingleElementAtPath_NotWholeArray()
    {
        Configure(deleteAfterSuccess: false); // default path $.current
        SetCurrentElement("keyA");
        // If the node ever fell back to reading the whole $.files array (e.g. a wrong or
        // omitted path), this is the value it would wrongly key off — must never happen.
        A.CallTo(() => _dataContext.Get<string>("$.files.key")).Returns("WRONG-ARRAY-KEY");
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer("keyA"));
        Assert.False(_state.WasKeptOnServer("WRONG-ARRAY-KEY"));
        A.CallTo(() => _dataContext.Get<string>("$.current.key")).MustHaveHappened();
        A.CallTo(() => _dataContext.Get<string>("$.files.key")).MustNotHaveHappened();
    }

    [Fact]
    public async Task ConfirmNode_DeleteMode_DeletesFullPathAndClearsPending()
    {
        Configure(deleteAfterSuccess: true);
        SetCurrentElement("keyB", fullPath: "/AR2.TXT");
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile("/AR2.TXT")).MustHaveHappenedOnceExactly();
        Assert.False(_state.HasPendingDelete("keyB"));
        A.CallTo(() => _sftp.Dispose()).MustHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ConfirmNode_KeepMode_MarksKeptOnServer()
    {
        Configure(deleteAfterSuccess: false);
        SetCurrentElement("keyC", fullPath: "/AR3.TXT");
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer("keyC"));
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ConfirmNode_DryRun_KeepMode_NeverMarksState()
    {
        // A dry-run confirms nothing: the write nodes upstream skipped their writes, so marking
        // the file "kept on server" would make every later REAL tick skip a file that was never
        // actually delivered — silent data loss for the pod's lifetime.
        Configure(deleteAfterSuccess: false);
        SetCurrentElement("keyE");
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.False(_state.WasKeptOnServer("keyE"));
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ConfirmNode_DryRun_DeleteMode_NeverTouchesServerOrState()
    {
        // Deleting during a dry-run would consume an LKV file whose content was never written
        // to WeClapp — the file must survive for the later real run.
        Configure(deleteAfterSuccess: true);
        SetCurrentElement("keyF", fullPath: "/AR9.TXT");
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        Assert.False(_state.HasPendingDelete("keyF"));
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ConfirmNode_DeleteMode_Failure_LeavesKeyPendingForRetry()
    {
        // Pending-delete bookkeeping means mark-BEFORE-delete (crash/failure safety net), not
        // mark-after — DilosFileFetchStep@1 must find the key still pending and retry it on the
        // next tick.
        Configure(deleteAfterSuccess: true);
        SetCurrentElement("keyD", fullPath: "/AR4.TXT");
        A.CallTo(() => _sftp.DeleteFile("/AR4.TXT")).Throws(new IOException("permission denied"));
        var sut = CreateSut();

        await Assert.ThrowsAsync<IOException>(() => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.True(_state.HasPendingDelete("keyD")); // marked BEFORE the attempt, never cleared
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }
}
