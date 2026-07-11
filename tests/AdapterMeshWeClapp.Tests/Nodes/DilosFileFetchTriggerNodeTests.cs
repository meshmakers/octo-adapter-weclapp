using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class DilosFileFetchTriggerNodeTests
{
    private readonly ITriggerContext _context = A.Fake<ITriggerContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpFileSystemFactory _sftpFactory = A.Fake<ISftpFileSystemFactory>();
    private readonly ISftpFileSystem _sftp = A.Fake<ISftpFileSystem>();
    private readonly List<JsonNode?> _executedDocuments = new();

    private static readonly SftpConnectionSettings PasswordSettings = new()
    {
        Host = "sftp.lkv.example",
        Username = "weclapp",
        Password = "secret",
    };

    private DilosFileFetchTriggerNode CreateSut(SftpConnectionSettings? settings = null)
    {
        A.CallTo(() => _context.NodeContext).Returns(_nodeContext);
        A.CallTo(() => _context.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp"))
            .Returns(settings ?? PasswordSettings);
        A.CallTo(() => _context.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call => _executedDocuments.Add((JsonNode?)call.Arguments[1]))
            .Returns(Task.FromResult<object?>(null));
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).Returns(_sftp);
        return new DilosFileFetchTriggerNode(A.Fake<ILogger<DilosFileFetchTriggerNode>>(), _sftpFactory);
    }

    private DilosFileFetchTriggerNodeConfiguration Configure(string pattern = "AR*TXT",
        string serverConfiguration = "LkvSftp", int minFileAgeSeconds = 60, bool deleteAfterSuccess = true)
    {
        var config = new DilosFileFetchTriggerNodeConfiguration
        {
            ServerConfiguration = serverConfiguration,
            FilePattern = pattern,
            MinFileAgeSeconds = minFileAgeSeconds,
            PollingIntervalSeconds = 900,
            DeleteAfterSuccess = deleteAfterSuccess,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>()).Returns(config);
        return config;
    }

    private static SftpFileEntry RemoteFile(string name, int ageMinutes = 10, long length = 100) =>
        new(name, "/" + name, false, DateTime.UtcNow.AddMinutes(-ageMinutes), length);

    private void ListingReturns(params SftpFileEntry[] entries) =>
        A.CallTo(() => _sftp.ListFiles("/")).Returns(entries);

    [Theory]
    [InlineData("AR*TXT", "AR00006946.TXT", true)]
    [InlineData("AR*TXT", "AR20240205143134947.TXT", true)]
    [InlineData("AR*TXT", "ar20240205143134947.txt", true)] // case-insensitive
    [InlineData("AR*TXT", "BE_20240205035403463.txt", false)]
    [InlineData("AR*TXT", "XAR00006946.TXT", false)] // anchored at start
    [InlineData("AR*TXT", "AR00006946.TXT.bak", false)] // anchored at end
    [InlineData("BE*txt", "BE_20240205035403463.txt", true)]
    [InlineData("AR????????.TXT", "AR00006946.TXT", true)] // '?' = exactly one char
    [InlineData("AR????????.TXT", "AR0000694.TXT", false)]
    public void GlobMatch_UsesBillbeeSemantics(string pattern, string fileName, bool expected)
    {
        Assert.Equal(expected, DilosFileFetchTriggerNode.GlobMatch(fileName, pattern));
    }

    [Fact]
    public async Task FetchOnce_ExecutesPerMatchingFileInNameOrderAndDeletesAfterSuccess()
    {
        Configure("AR*TXT");
        // Listing deliberately unsorted; contains a directory and a non-matching BE file.
        ListingReturns(
            RemoteFile("AR20240205143134947.TXT"),
            new SftpFileEntry("ARCHIVE.TXT", "/ARCHIVE.TXT", true, DateTime.UtcNow.AddDays(-1), 0),
            RemoteFile("BE_20240205035403463.txt"),
            RemoteFile("AR00006946.TXT"));
        A.CallTo(() => _sftp.DownloadText("/AR00006946.TXT")).Returns("K|content-1");
        A.CallTo(() => _sftp.DownloadText("/AR20240205143134947.TXT")).Returns("K|content-2");
        var sut = CreateSut();

        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, _executedDocuments.Count);
        Assert.Equal("AR00006946.TXT", _executedDocuments[0]!["fileName"]!.ToString());
        Assert.Equal("K|content-1", _executedDocuments[0]!["content"]!.ToString());
        Assert.Equal("AR20240205143134947.TXT", _executedDocuments[1]!["fileName"]!.ToString());
        Assert.Equal("K|content-2", _executedDocuments[1]!["content"]!.ToString());

        A.CallTo(() => _sftp.DeleteFile("/AR00006946.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _sftp.DeleteFile("/AR20240205143134947.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _sftp.DownloadText("/BE_20240205035403463.txt")).MustNotHaveHappened();
        A.CallTo(() => _sftp.DeleteFile("/BE_20240205035403463.txt")).MustNotHaveHappened();
        A.CallTo(() => _sftp.DeleteFile("/ARCHIVE.TXT")).MustNotHaveHappened();
        A.CallTo(() => _sftp.Dispose()).MustHaveHappened();
    }

    [Fact]
    public async Task FetchOnce_SkipsFilesYoungerThanMinAge()
    {
        Configure("AR*TXT", minFileAgeSeconds: 60);
        var young = new SftpFileEntry("AR_young.TXT", "/AR_young.TXT", false,
            DateTime.UtcNow.AddSeconds(-5), 100);
        ListingReturns(young, RemoteFile("AR_old.TXT"));
        A.CallTo(() => _sftp.DownloadText("/AR_old.TXT")).Returns("old");
        var sut = CreateSut();

        await sut.FetchOnceAsync(_context);

        var document = Assert.Single(_executedDocuments);
        Assert.Equal("AR_old.TXT", document!["fileName"]!.ToString());
        A.CallTo(() => _sftp.DownloadText("/AR_young.TXT")).MustNotHaveHappened();
        A.CallTo(() => _sftp.DeleteFile("/AR_young.TXT")).MustNotHaveHappened();
    }

    [Fact]
    public async Task FetchOnce_PipelineFailureLeavesFileOnServerAndContinuesWithNext()
    {
        Configure("AR*TXT");
        ListingReturns(RemoteFile("AR1.TXT"), RemoteFile("AR2.TXT"));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("bad");
        A.CallTo(() => _sftp.DownloadText("/AR2.TXT")).Returns("good");
        var sut = CreateSut();
        A.CallTo(() => _context.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call => _executedDocuments.Add((JsonNode?)call.Arguments[1]))
            .ReturnsLazily(call =>
                ((JsonNode?)call.Arguments[1])!["content"]!.ToString() == "bad"
                    ? Task.FromException<object?>(new InvalidOperationException("pipeline failed"))
                    : Task.FromResult<object?>(null));

        await sut.FetchOnceAsync(_context); // must not throw — per-file isolation

        Assert.Equal(2, _executedDocuments.Count); // both were attempted
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustNotHaveHappened(); // failed file stays
        A.CallTo(() => _sftp.DeleteFile("/AR2.TXT")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FetchOnce_DeleteFailureDoesNotReexecuteFileOnNextPollButRetriesDelete()
    {
        Configure("AR*TXT");
        var file = RemoteFile("AR1.TXT");
        ListingReturns(file);
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("content");
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT"))
            .Throws(new IOException("permission denied")).Once();
        var sut = CreateSut();

        await sut.FetchOnceAsync(_context); // poll 1: executed, delete failed
        await sut.FetchOnceAsync(_context); // poll 2: same file still listed

        Assert.Single(_executedDocuments); // NOT executed a second time
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustHaveHappenedTwiceExactly(); // delete retried
    }

    [Fact]
    public async Task FetchOnce_KeepMode_ExecutesOnceAndNeverDeletes()
    {
        // deleteAfterSuccess=false (dry-run pipelines): the execution succeeds without
        // writing to WeClapp, so the file must survive on the server — and must not be
        // re-executed on every poll while it is unchanged.
        Configure("AR*TXT", deleteAfterSuccess: false);
        var sut = CreateSut();

        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 20));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("first");
        await sut.FetchOnceAsync(_context); // poll 1: executed, kept
        await sut.FetchOnceAsync(_context); // poll 2: unchanged file → skipped

        Assert.Single(_executedDocuments);

        // A NEW file under the same name (different mtime/size) is executed again.
        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 5, length: 222));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("second");
        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, _executedDocuments.Count);
        Assert.Equal("second", _executedDocuments[1]!["content"]!.ToString());
        A.CallTo(() => _sftp.DeleteFile(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FetchOnce_FlipToDeleteMode_ReexecutesKeptFiles()
    {
        // Files executed during the keep-mode (dry-run) phase must be REALLY processed and
        // consumed after the go-live flip — stale keep-keys must not suppress them.
        Configure("AR*TXT", deleteAfterSuccess: false);
        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 20));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("content");
        var sut = CreateSut();
        await sut.FetchOnceAsync(_context); // validation phase: executed, kept

        Configure("AR*TXT", deleteAfterSuccess: true); // go-live flip on the same instance
        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, _executedDocuments.Count);
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FetchOnce_FlipToKeepMode_NeverFiresStaleDeleteRetry()
    {
        // A pending delete-retry key from the delete-mode phase must not delete files
        // after the trigger switched to keep mode (e.g. a rollback to validation).
        Configure("AR*TXT", deleteAfterSuccess: true);
        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 20));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("content");
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT"))
            .Throws(new IOException("permission denied")).Once();
        var sut = CreateSut();
        await sut.FetchOnceAsync(_context); // executed, delete failed → retry key pending

        Configure("AR*TXT", deleteAfterSuccess: false); // rollback to validation mode
        await sut.FetchOnceAsync(_context);

        // Re-executed under keep semantics (downstream is idempotent) instead of deleting.
        Assert.Equal(2, _executedDocuments.Count);
        A.CallTo(() => _sftp.DeleteFile("/AR1.TXT")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FetchOnce_SameNameWithNewTimestampIsProcessedAgainAfterDelete()
    {
        Configure("AR*TXT");
        var sut = CreateSut();

        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 20));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("first");
        await sut.FetchOnceAsync(_context);

        // DILOS writes a NEW file under the same name (different mtime/size).
        ListingReturns(RemoteFile("AR1.TXT", ageMinutes: 5, length: 222));
        A.CallTo(() => _sftp.DownloadText("/AR1.TXT")).Returns("second");
        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, _executedDocuments.Count);
        Assert.Equal("first", _executedDocuments[0]!["content"]!.ToString());
        Assert.Equal("second", _executedDocuments[1]!["content"]!.ToString());
    }

    [Fact]
    public async Task FetchOnce_ServerConfigurationNotDefinedThrows()
    {
        Configure(serverConfiguration: "MissingEntry");
        A.CallTo(() => _globalConfiguration.IsDefined("MissingEntry")).Returns(false);
        var sut = CreateSut();

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));
    }

    [Fact]
    public async Task FetchOnce_MissingAuthenticationThrows()
    {
        Configure();
        var sut = CreateSut(new SftpConnectionSettings
        {
            Host = "sftp.lkv.example",
            Username = "weclapp", // neither Password nor PrivateKey
        });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));
    }

    [Fact]
    public async Task StartAndStop_TerminateCleanly()
    {
        Configure("AR*TXT");
        ListingReturns();
        var sut = CreateSut();

        await sut.StartAsync(_context);
        await Task.Delay(50); // let the first poll run
        var stop = sut.StopAsync(_context);
        var finished = await Task.WhenAny(stop, Task.Delay(5000));

        Assert.Same(stop, finished); // stop must not hang
        A.CallTo(() => _sftp.ListFiles("/")).MustHaveHappened();
    }
}
