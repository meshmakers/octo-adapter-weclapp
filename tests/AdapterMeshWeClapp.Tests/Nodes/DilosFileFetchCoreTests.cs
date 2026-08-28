using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>
/// The scope/key format the AR/BE return path is keyed on. <c>DilosFileGate@1</c> builds a key from
/// it and <c>DilosFileConfirm@1</c> carries that key through the data context, so its escaping is
/// what keeps one pipeline's marks out of the other's scope inside the shared state singleton.
/// </summary>
public class DilosFileFetchCoreTests
{
    [Fact]
    public void ScopePrefix_SeparatorInsideComponents_CannotShiftComponentBoundaries()
    {
        // '|' is both the scope-component separator and the FileKey field separator, and
        // PruneScopeTo matches scopes by StartsWith — two different triples must never yield
        // one prefix, and no prefix may prefix-match another scope's prefix.
        var a = DilosFileFetchCore.ScopePrefix("LkvSftp", "/a", "b|AR*TXT");
        var b = DilosFileFetchCore.ScopePrefix("LkvSftp", "/a|b", "AR*TXT");
        var plain = DilosFileFetchCore.ScopePrefix("LkvSftp", "/", "AR*TXT");
        var extended = DilosFileFetchCore.ScopePrefix("LkvSftp", "/", "AR*TXT|old");

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
        Assert.Equal("LkvSftp|/|AR*TXT|", DilosFileFetchCore.ScopePrefix("LkvSftp", "/", "AR*TXT"));
    }

    [Fact]
    public void ScopePrefix_BackslashBeforeASeparator_CannotForgeAComponentBoundary()
    {
        // A component ending in '\' would otherwise escape the separator that follows it and
        // merge two components into one, which is the same collision the '|' escaping prevents.
        Assert.NotEqual(
            DilosFileFetchCore.ScopePrefix("LkvSftp", @"/a\", "AR*TXT"),
            DilosFileFetchCore.ScopePrefix("LkvSftp", "/a", @"\AR*TXT"));
    }
}
