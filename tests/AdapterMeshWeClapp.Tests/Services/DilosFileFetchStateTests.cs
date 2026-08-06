using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Services;

/// <summary>
/// <see cref="DilosFileFetchState"/> is ONE DI singleton shared by every pipeline that uses
/// <c>DilosFileFetchStep@1</c>/<c>DilosFileConfirm@1</c> — today that means BOTH the ar and the
/// be pipeline resolve the same instance (<c>Program.cs</c>). <see cref="DilosFileFetchState.IntersectWith"/>
/// must therefore prune only the keys that belong to the calling pipeline's OWN scope (M1): before
/// the scope parameter existed, an ar tick's <c>IntersectWith(files.Select(FileKey))</c> intersected
/// BOTH global sets against ar's file list alone, discarding every be key ar never listed (and
/// vice versa) — a keep-mode file would be re-emitted/re-executed on the very next alternating
/// tick, and a delete-mode pending-retry key would be lost, turning a delete-only retry into a
/// full re-execution.
/// </summary>
public class DilosFileFetchStateTests
{
    private const string ScopeA = "LkvSftp|/|AR*TXT|";
    private const string ScopeB = "LkvSftp|/|BE*txt|";

    [Fact]
    public void IntersectWith_DisjointOtherScope_LeavesThisScopesKeysUntouched()
    {
        var state = new DilosFileFetchState();
        var keptA = ScopeA + "AR1.TXT|100|1000";
        var pendingA = ScopeA + "AR2.TXT|200|2000";
        state.MarkKeptOnServer(keptA);
        state.MarkPendingDelete(pendingA);

        // A be tick runs IntersectWith for ITS OWN scope with keys that share nothing with ar's —
        // exactly what happens every time the two pipelines' cron ticks alternate.
        state.IntersectWith(ScopeB, new[] { ScopeB + "BE1.txt|50|500" });

        Assert.True(state.WasKeptOnServer(keptA));
        Assert.True(state.HasPendingDelete(pendingA));
    }

    [Fact]
    public void IntersectWith_SameScope_PrunesOnlyThisScopesVanishedKeys()
    {
        var state = new DilosFileFetchState();
        var stillPresentA = ScopeA + "AR1.TXT|100|1000";
        var vanishedA = ScopeA + "AR2.TXT|200|2000";
        var untouchedB = ScopeB + "BE1.txt|50|500";
        state.MarkKeptOnServer(stillPresentA);
        state.MarkKeptOnServer(vanishedA);
        state.MarkKeptOnServer(untouchedB);

        // ar's own tick: only stillPresentA is still on the server.
        state.IntersectWith(ScopeA, new[] { stillPresentA });

        Assert.True(state.WasKeptOnServer(stillPresentA)); // still listed — survives
        Assert.False(state.WasKeptOnServer(vanishedA)); // vanished from ar's own listing — pruned
        Assert.True(state.WasKeptOnServer(untouchedB)); // different scope entirely — never touched
    }
}
