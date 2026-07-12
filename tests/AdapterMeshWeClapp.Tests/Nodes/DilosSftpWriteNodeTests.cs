using System.Text;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// DilosSftpWrite@1 — the Latin-1 delivery counterpart to the built-in SftpUpload@1.
/// Golden AS/AI files are ISO-8859-1 (DILOS-import-proven); SftpUpload@1 writes UTF-8,
/// which breaks umlauts (live-measured: 32/48 articles, 45/89 customers carry non-ASCII).
/// </summary>
public class DilosSftpWriteNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpFileSystemFactory _sftpFactory = A.Fake<ISftpFileSystemFactory>();
    private readonly ISftpFileSystem _sftp = A.Fake<ISftpFileSystem>();
    private readonly SftpConnectionSettings _settings = new() { Host = "h", Username = "u", Password = "p" };
    private readonly DilosSftpWriteNode _sut;

    public DilosSftpWriteNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp")).Returns(_settings);
        A.CallTo(() => _sftpFactory.Connect(_settings)).Returns(_sftp);
        _sut = new DilosSftpWriteNode(_next, _etlContext, _sftpFactory);
    }

    private DilosSftpWriteNodeConfiguration Configure(string remoteDirectory = "/",
        string serverConfiguration = "LkvSftp")
    {
        var config = new DilosSftpWriteNodeConfiguration
        {
            ServerConfiguration = serverConfiguration,
            RemoteDirectory = remoteDirectory,
            FileNamePath = "$.dilosFileName",
            Path = "$.dilos",
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosSftpWriteNodeConfiguration>()).Returns(config);
        return config;
    }

    private void SetDocument(string? fileName, string? content)
    {
        A.CallTo(() => _dataContext.Get<string>("$.dilosFileName")).Returns(fileName);
        A.CallTo(() => _dataContext.Get<string>("$.dilos")).Returns(content);
    }

    [Fact]
    public async Task ProcessObjectAsync_UploadsContentAsLatin1Bytes()
    {
        Configure();
        // Pure Latin-1 content: umlauts + ß must land as SINGLE bytes (ü=0xFC, ß=0xDF),
        // exactly like the golden Billbee-produced files — not as UTF-8 double bytes.
        const string content = "K*|Müller|Große Straße 5|Rösrath\n";
        SetDocument("AI5910986621265.txt", content);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var expected = Encoding.Latin1.GetBytes(content);
        A.CallTo(() => _sftp.UploadBytes("/AI5910986621265.txt",
                A<byte[]>.That.IsSameSequenceAs(expected)))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _sftp.Dispose()).MustHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_ReplacesNonLatin1CharsLoudly()
    {
        Configure();
        // € (U+20AC) and the en dash (U+2013) do not exist in ISO-8859-1 → '?' with a
        // warning; silent corruption is the failure mode this node exists to prevent.
        SetDocument("AS20260205143134.txt", "Preis 10€ – fertig\n");

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var expected = Encoding.Latin1.GetBytes("Preis 10? ? fertig\n");
        A.CallTo(() => _sftp.UploadBytes("/AS20260205143134.txt",
                A<byte[]>.That.IsSameSequenceAs(expected)))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PureLatin1ContentLogsNoWarning()
    {
        Configure();
        SetDocument("AS20260205143134.txt", "A*||123|Ersatzglas VOLT\n");

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_JoinsRemoteDirectoryWithoutDoubleSlash()
    {
        Configure(remoteDirectory: "/out/");
        SetDocument("AI1.txt", "K*|x\n");

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.UploadBytes("/out/AI1.txt", A<byte[]>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_SdkDryRun_SkipsUploadButContinues()
    {
        Configure();
        SetDocument("AI1.txt", "K*|x\n");
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingFileName_ThrowsWithoutConnecting()
    {
        Configure();
        SetDocument(fileName: null, content: "K*|x\n");

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyContent_ThrowsWithoutConnecting()
    {
        // An empty DILOS file would be a false snapshot — upstream must never emit one
        // (the Batch trigger skips empty polls), so reaching this node empty is a bug.
        Configure();
        SetDocument("AS20260205143134.txt", "");

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownServerConfiguration_Throws()
    {
        Configure(serverConfiguration: "MissingConfig");
        SetDocument("AI1.txt", "K*|x\n");
        A.CallTo(() => _globalConfiguration.IsDefined("MissingConfig")).Returns(false);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ConfigurationWithoutCredentials_ThrowsBeforeConnecting()
    {
        // Same guard as DilosFileFetch: a half-configured entry must fail with the clear
        // domain message on BOTH transfer directions, not reach SSH.NET with an empty password.
        Configure();
        SetDocument("AI1.txt", "K*|x\n");
        A.CallTo(() => _globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp"))
            .Returns(new SftpConnectionSettings { Host = "h", Username = "u" });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("../AI1.txt")]
    [InlineData("sub/AI1.txt")]
    [InlineData("sub\\AI1.txt")]
    public async Task ProcessObjectAsync_FileNameWithPathSegments_ThrowsWithoutConnecting(string fileName)
    {
        Configure();
        SetDocument(fileName, "K*|x\n");

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_SdkDryRun_StillReportsNonLatin1Characters()
    {
        // The dry-run is the pre-go-live validation pass — encoding loss must surface
        // there too, otherwise the validation certifies a file the real run would mangle.
        Configure();
        SetDocument("AS20260205143134.txt", "Preis 10€\n");
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._)).MustHaveHappened();
        A.CallTo(() => _sftpFactory.Connect(A<SftpConnectionSettings>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AstralCharacterCountsOnceAndEncodesAsSingleQuestionMark()
    {
        // 😀 (U+1F600) is a surrogate PAIR in UTF-16 but ONE scalar — the warning must
        // report it once as U+1F600 (not two raw surrogates) and the bytes carry one '?'.
        Configure();
        SetDocument("AI1.txt", "Hi 😀 end\n");
        object?[]? warningArgs = null;
        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._))
            .Invokes(call => warningArgs = (object?[]?)call.Arguments[1]);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _sftp.UploadBytes("/AI1.txt",
                A<byte[]>.That.IsSameSequenceAs(Encoding.Latin1.GetBytes("Hi ? end\n"))))
            .MustHaveHappenedOnceExactly();
        Assert.NotNull(warningArgs);
        Assert.Equal(1, warningArgs![0]);                       // one scalar, not two code units
        Assert.Equal("U+1F600", warningArgs[2]!.ToString());    // the real scalar, not U+D83D U+DE00
    }
}
