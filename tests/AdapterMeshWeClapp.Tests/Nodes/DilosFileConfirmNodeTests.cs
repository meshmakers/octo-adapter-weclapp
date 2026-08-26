using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// DilosFileConfirm@1 — the LAST child inside the per-file <c>ForEach@1</c>. Everything it needs
/// beyond its path comes from the element <c>DilosFileGate@1</c> stamped, so these tests run
/// against a REAL <see cref="DataContextImpl" /> holding a stamped element: a faked data context
/// would answer for paths the gate never writes, which is exactly what must not be assumed.
/// </summary>
public class DilosFileConfirmNodeTests
{
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

    private DilosFileConfirmNodeConfiguration Configure(string path = "$.current")
    {
        var config = new DilosFileConfirmNodeConfiguration { Path = path };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosFileConfirmNodeConfiguration>()).Returns(config);
        return config;
    }

    /// <summary>One element in the shape <c>DilosFileGate@1</c> hands on: the listing's own
    /// fields plus the key, the mode and the server the gate stamped.</summary>
    private static string Stamped(string key, bool deleteAfterSuccess, string fullPath = "/AR1.TXT",
        string name = "AR1.TXT")
    {
        var mode = deleteAfterSuccess ? "true" : "false";
        return "{\"name\":\"" + name + "\",\"fullPath\":\"" + fullPath + "\",\"key\":\"" + key +
               "\",\"deleteAfterSuccess\":" + mode + ",\"serverConfiguration\":\"LkvSftp\"}";
    }

    /// <summary>The iteration context the per-file <c>ForEach@1</c> runs its children in: the
    /// current element at <c>$.current</c>, the whole array still reachable at <c>$.files</c>.</summary>
    private static DataContextImpl IterationOf(string current, string? otherInArray = null)
    {
        return new DataContextImpl(JsonDocument.Parse(
            "{\"current\":" + current + ",\"files\":[" + (otherInArray ?? current) + "]}"));
    }

    [Fact]
    public async Task KeepMode_TakesTheModeFromTheElementAndMarksItKept()
    {
        Configure();
        using var dataContext = IterationOf(Stamped("keyC", deleteAfterSuccess: false));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer("keyC"));
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DeleteMode_TakesTheModeFromTheElementAndDeletesTheFile()
    {
        // The mode is configured once, on the gate. Reading it off the element is what makes a
        // half flip - delete on one node, keep on the other - impossible to express.
        Configure();
        using var dataContext = IterationOf(Stamped("keyB", deleteAfterSuccess: true, fullPath: "/AR2.TXT"));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile("/AR2.TXT")).MustHaveHappenedOnceExactly();
        Assert.False(_state.HasPendingDelete("keyB"));
        A.CallTo(() => _sftp.Dispose()).MustHaveHappened();
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DeleteMode_ConnectsToTheServerTheElementNames()
    {
        Configure();
        using var dataContext = IterationOf(Stamped("keyB", deleteAfterSuccess: true));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RefusesAnElementWithoutAModeStamp()
    {
        // Defaulting would pick one of the two behaviours silently: either an LKV file is deleted
        // although nothing was written, or a delivered file is kept and delivered again.
        Configure();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(
            "{\"current\":{\"name\":\"AR1.TXT\",\"fullPath\":\"/AR1.TXT\",\"key\":\"keyX\"}}"));
        var sut = CreateSut();

        var error = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.Contains("deleteAfterSuccess", error.Message);
        Assert.False(_state.WasKeptOnServer("keyX"));
    }

    [Fact]
    public async Task ReadsTheSingleElementAtItsPath_NotTheWholeArray()
    {
        // A path resolving to the whole array would confirm - and in delete mode remove - files
        // whose iteration has not even run yet.
        Configure();
        using var dataContext = IterationOf(Stamped("keyA", deleteAfterSuccess: false),
            Stamped("WRONG-ARRAY-KEY", deleteAfterSuccess: false));
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.True(_state.WasKeptOnServer("keyA"));
        Assert.False(_state.WasKeptOnServer("WRONG-ARRAY-KEY"));
    }

    [Fact]
    public async Task RefusesToConfirmWithoutAKey()
    {
        Configure();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(
            "{\"current\":{\"name\":\"AR1.TXT\",\"fullPath\":\"/AR1.TXT\",\"deleteAfterSuccess\":false}}"));
        var sut = CreateSut();

        var error = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.Contains("key", error.Message);
    }

    [Fact]
    public async Task DryRun_KeepMode_NeverMarksState()
    {
        // The write nodes upstream skipped their writes, so marking the file kept would make
        // every later REAL tick skip a file that was never delivered.
        Configure();
        using var dataContext = IterationOf(Stamped("keyE", deleteAfterSuccess: false));
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        Assert.False(_state.WasKeptOnServer("keyE"));
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DryRun_DeleteMode_NeverTouchesServerOrState()
    {
        // Deleting during a dry-run would consume an LKV file whose content was never written.
        Configure();
        using var dataContext = IterationOf(Stamped("keyF", deleteAfterSuccess: true, fullPath: "/AR9.TXT"));
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var sut = CreateSut();

        await sut.ProcessObjectAsync(dataContext, _nodeContext);

        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
        Assert.False(_state.HasPendingDelete("keyF"));
        A.CallTo(() => _next(dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DeleteFailure_LeavesTheKeyPendingForTheGateToRetry()
    {
        // Mark BEFORE the attempt: a failure - or a pod that dies mid-call - must still leave the
        // delete retryable on the next tick.
        Configure();
        using var dataContext = IterationOf(Stamped("keyD", deleteAfterSuccess: true, fullPath: "/AR4.TXT"));
        A.CallTo(() => _sftp.DeleteFile("/AR4.TXT")).Throws(new IOException("permission denied"));
        var sut = CreateSut();

        await Assert.ThrowsAsync<IOException>(() => sut.ProcessObjectAsync(dataContext, _nodeContext));

        Assert.True(_state.HasPendingDelete("keyD"));
        A.CallTo(() => _next(dataContext, _nodeContext)).MustNotHaveHappened();
    }
}
