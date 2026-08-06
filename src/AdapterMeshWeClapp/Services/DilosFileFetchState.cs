namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// Cross-tick memory shared between <c>DilosFileFetchStep@1</c> and <c>DilosFileConfirm@1</c>
/// (AR/BE return path, AB#4228/G2 cron-trigger redesign) as a DI singleton
/// (<c>Program.cs</c>). The pipeline engine constructs a fresh node instance per chain — one
/// per tick — so the two <c>HashSet&lt;string&gt;</c> instance fields that used to live directly
/// on <see cref="Nodes.DilosFileFetchTriggerNode"/> (constructed once for the lifetime of the
/// polling loop) would lose their state between ticks if they stayed on the node; they move
/// here instead. A pod restart clears this singleton exactly like restarting the old trigger
/// cleared its instance fields — same behavior, only the storage location changed.
/// <para/>
/// ONE singleton instance is shared by EVERY pipeline that wires up these two nodes — today
/// that means both the ar AND the be pipeline resolve the SAME instance, unlike the legacy
/// trigger, where each pipeline's <c>DilosFileFetchTriggerNode</c> owned its own instance
/// fields. Both sets are therefore keyed by a per-pipeline SCOPED key: a scope prefix
/// (<c>{ServerConfiguration}|{RemoteDirectory}|{FilePattern}|</c>, computed by
/// <c>DilosFileFetchStepNode</c> from its own config) followed by the file's <c>FileKey</c>
/// (<c>{Name}|{Length}|{LastWriteTimeUtc.Ticks}</c>, <see cref="Nodes.DilosFileFetchTriggerNode"/>)
/// — so ar's and be's keys never collide even though they share one <c>HashSet&lt;string&gt;</c>
/// pair. <c>DilosFileConfirm@1</c> is the only node that MARKS a key processed (kept-on-server or
/// pending-delete) — it receives the already-scoped key opaquely via the data context and never
/// needs the scope prefix itself. <c>DilosFileFetchStep@1</c> only reads those marks (to skip a
/// file or retry a delete) but writes both sets too, during its own listing — bounding them via
/// <see cref="IntersectWith"/> (scoped to ITS OWN prefix, so one pipeline's tick can never prune
/// another pipeline's keys) and clearing a delete it just retried via
/// <see cref="ClearPendingDelete"/>.
/// </summary>
public sealed class DilosFileFetchState
{
    private readonly object _gate = new();

    // Files confirmed processed in keep mode (deleteAfterSuccess=false): stay on the server,
    // must not be re-emitted every tick while the file is unchanged.
    private readonly HashSet<string> _keptOnServer = new(StringComparer.Ordinal);

    // Files confirmed processed whose remote delete failed: never re-emitted/re-executed, only
    // the delete itself is retried — by DilosFileFetchStep@1, during its next listing.
    private readonly HashSet<string> _pendingDelete = new(StringComparer.Ordinal);

    /// <summary>True if <paramref name="key"/> was confirmed kept on the server in an earlier
    /// tick (keep mode) — <c>DilosFileFetchStep@1</c> must skip it instead of re-emitting it.</summary>
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
    /// failed — <c>DilosFileFetchStep@1</c> must retry just the delete during listing, without
    /// re-emitting or re-executing the file.</summary>
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

    /// <summary>Forgets keys of files that vanished from the server, so both sets stay bounded —
    /// scoped to <paramref name="scopePrefix"/> only, since the singleton is shared across every
    /// pipeline wired to these nodes (see the class summary): this is per-SCOPE cleanup, not a
    /// plain global intersect, because a global intersect would also discard every OTHER
    /// pipeline's keys that this call's <paramref name="currentKeys"/> naturally never mentions.
    /// A key not starting with <paramref name="scopePrefix"/> is never touched, no matter what it
    /// is. Mirrors <c>DilosFileFetchTriggerNode.FetchOnceAsync</c>'s per-poll cleanup
    /// (<c>DilosFileFetchTriggerNode.cs:156-158</c>), which needed no scoping because each
    /// pipeline there owned its own instance fields instead of sharing one singleton.</summary>
    public void IntersectWith(string scopePrefix, IEnumerable<string> currentKeys)
    {
        var keys = currentKeys as HashSet<string> ?? new HashSet<string>(currentKeys, StringComparer.Ordinal);
        lock (_gate)
        {
            _keptOnServer.RemoveWhere(key => key.StartsWith(scopePrefix, StringComparison.Ordinal) && !keys.Contains(key));
            _pendingDelete.RemoveWhere(key => key.StartsWith(scopePrefix, StringComparison.Ordinal) && !keys.Contains(key));
        }
    }
}
