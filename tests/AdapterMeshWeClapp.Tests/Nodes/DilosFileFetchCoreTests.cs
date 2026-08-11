using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// The shared listing/key surface both DILOS fetch node families rely on. GlobMatch itself is
/// pinned in <see cref="DilosFileFetchTriggerNodeTests"/> (Billbee semantics theory).
/// </summary>
public class DilosFileFetchCoreTests
{
    private static DilosFileFetchStepNodeConfiguration Config(string server, string dir, string pattern) =>
        new() { ServerConfiguration = server, RemoteDirectory = dir, FilePattern = pattern };

    [Fact]
    public void ScopePrefix_SeparatorInsideComponents_CannotShiftComponentBoundaries()
    {
        // '|' is both the scope-component separator and the FileKey field separator, and
        // PruneScopeTo matches scopes by StartsWith — two different triples must never yield
        // one prefix, and no prefix may prefix-match another scope's prefix.
        var a = DilosFileFetchCore.ScopePrefix(Config("LkvSftp", "/a", "b|AR*TXT"));
        var b = DilosFileFetchCore.ScopePrefix(Config("LkvSftp", "/a|b", "AR*TXT"));
        var plain = DilosFileFetchCore.ScopePrefix(Config("LkvSftp", "/", "AR*TXT"));
        var extended = DilosFileFetchCore.ScopePrefix(Config("LkvSftp", "/", "AR*TXT|old"));

        Assert.NotEqual(a, b);
        Assert.False(extended.StartsWith(plain, StringComparison.Ordinal));
        Assert.False(plain.StartsWith(extended, StringComparison.Ordinal));
    }

    [Fact]
    public void ScopePrefix_PlainComponents_KeepTheDocumentedFormat()
    {
        // Shipped configs carry no '|' or '\' — their prefixes must stay byte-identical to the
        // documented {server}|{dir}|{pattern}| format (process-local keys, but stability keeps
        // logs and tests readable).
        Assert.Equal("LkvSftp|/|AR*TXT|", DilosFileFetchCore.ScopePrefix(Config("LkvSftp", "/", "AR*TXT")));
    }

    [Fact]
    public void ListMatchingFiles_MissingFilePattern_FailsWithClearConfigError()
    {
        // The pipeline YAML deserializer does not enforce C# 'required' — a yaml omitting
        // filePattern deploys cleanly, so the node must fail with a clear message instead of a
        // NullReferenceException from the glob regex.
        var config = new DilosFileFetchStepNodeConfiguration
        {
            ServerConfiguration = "LkvSftp",
            FilePattern = null!,
        };

        var ex = Assert.Throws<WeClappPipelineExecutionException>(
            () => DilosFileFetchCore.ListMatchingFiles(A.Fake<ISftpFileSystem>(), config));
        Assert.Contains("filePattern", ex.Message);
    }
}
