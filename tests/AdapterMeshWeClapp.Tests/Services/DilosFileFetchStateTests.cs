using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Services;

/// <summary>
/// <see cref="DilosFileFetchState"/> is ONE DI singleton shared by every pipeline that uses
/// <c>DilosFileFetchStep@1</c>/<c>DilosFileConfirm@1</c> — today that means BOTH the ar and the
/// be pipeline resolve the same instance (<c>Program.cs</c>). <see cref="DilosFileFetchState.PruneScopeTo"/>
/// must therefore prune only the keys that belong to the calling pipeline's OWN scope: before
/// the scope parameter existed, an ar tick's prune call intersected
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
    public void PruneScopeTo_DisjointOtherScope_LeavesThisScopesKeysUntouched()
    {
        var state = new DilosFileFetchState();
        var keptA = ScopeA + "AR1.TXT|100|1000";
        var pendingA = ScopeA + "AR2.TXT|200|2000";
        state.MarkKeptOnServer(keptA);
        state.MarkPendingDelete(pendingA);

        // A be tick runs PruneScopeTo for ITS OWN scope with keys that share nothing with ar's —
        // exactly what happens every time the two pipelines' cron ticks alternate.
        state.PruneScopeTo(ScopeB, new[] { ScopeB + "BE1.txt|50|500" });

        Assert.True(state.WasKeptOnServer(keptA));
        Assert.True(state.HasPendingDelete(pendingA));
    }

    [Fact]
    public void PruneScopeTo_CallerSetWithNonOrdinalComparer_StillComparesOrdinally()
    {
        var state = new DilosFileFetchState();
        var keptA = ScopeA + "AR1.TXT|100|1000";
        state.MarkKeptOnServer(keptA);

        // The exact (ordinal) key vanished from the server; a caller's case-insensitive set
        // "contains" it only under ITS OWN comparer — pruning must stay ordinal regardless of
        // what collection type/comparer the caller happens to pass.
        var lowerCased = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ScopeA + "ar1.txt|100|1000" };
        state.PruneScopeTo(ScopeA, lowerCased);

        Assert.False(state.WasKeptOnServer(keptA));
    }

    [Fact]
    public void PruneScopeTo_SameScope_PrunesOnlyThisScopesVanishedKeys()
    {
        var state = new DilosFileFetchState();
        var stillPresentA = ScopeA + "AR1.TXT|100|1000";
        var vanishedA = ScopeA + "AR2.TXT|200|2000";
        var untouchedB = ScopeB + "BE1.txt|50|500";
        state.MarkKeptOnServer(stillPresentA);
        state.MarkKeptOnServer(vanishedA);
        state.MarkKeptOnServer(untouchedB);

        // ar's own tick: only stillPresentA is still on the server.
        state.PruneScopeTo(ScopeA, new[] { stillPresentA });

        Assert.True(state.WasKeptOnServer(stillPresentA)); // still listed — survives
        Assert.False(state.WasKeptOnServer(vanishedA)); // vanished from ar's own listing — pruned
        Assert.True(state.WasKeptOnServer(untouchedB)); // different scope entirely — never touched
    }
}
