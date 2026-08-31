namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// Cross-tick memory shared between <c>DilosFileGate@1</c> and <c>DilosFileConfirm@1</c>
/// (AR/BE return path) as a DI singleton (<c>Program.cs</c>). The pipeline engine constructs a
/// fresh node instance per chain - one per tick - so which files an earlier tick already
/// processed cannot live on the nodes themselves; it lives here. A pod restart clears the
/// singleton, and a kept file is then let through once more (downstream idempotency covers
/// that). A pipeline-level REdeploy does NOT clear it: the singleton survives until the pod
/// itself restarts, so stale marks outlive a redeploy.
/// <para/>
/// ONE singleton instance is shared by EVERY pipeline that wires up these two nodes — today
/// that means both the ar AND the be pipeline resolve the SAME instance. Both sets are
/// therefore keyed by a per-pipeline SCOPED key: a scope prefix
/// (<c>{ServerConfiguration}|{RemoteDirectory}|{FilePattern}|</c> with '\'/'|' escaped inside
/// components, <see cref="Nodes.DilosFileFetchCore.ScopePrefix"/> over the triple the gate reads
/// off the listed element) followed by the file's
/// <see cref="Nodes.DilosFileFetchCore.FileKey"/> (<c>{Name}|{Length}|{LastWriteTimeUtc}</c>)
/// — so ar's and be's keys never collide even though they share one <c>HashSet&lt;string&gt;</c>
/// pair. <c>DilosFileConfirm@1</c> is the only node that MARKS a key processed (kept-on-server or
/// pending-delete) — it receives the already-scoped key opaquely via the data context and never
/// needs the scope prefix itself. <c>DilosFileGate@1</c> only reads those marks (to drop a file
/// or settle an owed delete) but writes both sets too, while filtering a listing - bounding them
/// via <see cref="PruneScopeTo"/> (scoped to the prefixes of the elements it was handed, so one
/// pipeline's tick can never prune another pipeline's keys) and clearing a delete it just
/// settled via <see cref="ClearPendingDelete"/>.
/// </summary>
public sealed class DilosFileFetchState
{
    private readonly object _gate = new();

    // Files confirmed processed in keep mode (deleteAfterSuccess=false): stay on the server,
    // must not be let through every tick while the file is unchanged.
    private readonly HashSet<string> _keptOnServer = new(StringComparer.Ordinal);

    // Files confirmed processed whose remote delete failed: never let through or re-executed,
    // only the delete itself is settled - by DilosFileGate@1, on its next listing.
    private readonly HashSet<string> _pendingDelete = new(StringComparer.Ordinal);

    /// <summary>True if <paramref name="key"/> was confirmed kept on the server in an earlier
    /// tick (keep mode) - <c>DilosFileGate@1</c> must drop it instead of letting it through again.</summary>
    public bool WasKeptOnServer(string key)
    {
        lock (_gate)
        {
            return _keptOnServer.Contains(key);
        }
    }

    /// <summary>Marks <paramref name="key"/> as confirmed processed and kept on the server
    /// (keep mode). Called by <c>DilosFileConfirm@1</c> only.</summary>
    public void MarkKeptOnServer(string key)
    {
        lock (_gate)
        {
            _keptOnServer.Add(key);
        }
    }

    /// <summary>True if <paramref name="key"/> was confirmed processed but its remote delete
    /// failed - <c>DilosFileGate@1</c> must settle just the delete while filtering, without
    /// letting the file through or re-executing it.</summary>
    public bool HasPendingDelete(string key)
    {
        lock (_gate)
        {
            return _pendingDelete.Contains(key);
        }
    }

    /// <summary>Marks <paramref name="key"/> as pending delete BEFORE the delete attempt, so a
    /// failed (or interrupted) attempt still leaves the key retryable on the next listing.</summary>
    public void MarkPendingDelete(string key)
    {
        lock (_gate)
        {
            _pendingDelete.Add(key);
        }
    }

    /// <summary>Clears the pending-delete marker after a successful delete.</summary>
    public void ClearPendingDelete(string key)
    {
        lock (_gate)
        {
            _pendingDelete.Remove(key);
        }
    }

    /// <summary>Prunes the <paramref name="scopePrefix"/> scope down to <paramref name="currentKeys"/>:
    /// forgets this scope's keys for files that vanished from the server, so both sets stay
    /// bounded. Scoped because the singleton is shared across every pipeline wired to these
    /// nodes (see the class summary) — a global intersect would also discard every OTHER
    /// pipeline's keys that this call's <paramref name="currentKeys"/> naturally never mentions.
    /// A key not starting with <paramref name="scopePrefix"/> is never touched, no matter what it
    /// is. The gate derives the scopes it prunes from the elements it was handed, so an EMPTY
    /// listing prunes nothing at all - see the accepted residue recorded in CLAUDE.md.</summary>
    public void PruneScopeTo(string scopePrefix, IEnumerable<string> currentKeys)
    {
        // Always rebuild with the ordinal comparer: pruning must compare keys ordinally no
        // matter what collection type/comparer the caller happens to pass.
        var keys = new HashSet<string>(currentKeys, StringComparer.Ordinal);
        lock (_gate)
        {
            _keptOnServer.RemoveWhere(key => key.StartsWith(scopePrefix, StringComparison.Ordinal) && !keys.Contains(key));
            _pendingDelete.RemoveWhere(key => key.StartsWith(scopePrefix, StringComparison.Ordinal) && !keys.Contains(key));
        }
    }
}
