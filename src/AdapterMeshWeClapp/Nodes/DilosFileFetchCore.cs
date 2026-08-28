namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// The DILOS file identity and scope format shared by <see cref="DilosFileGateNode"/> (which builds
/// a key per listed file) and the <see cref="Services.DilosFileFetchState"/> key contract (which
/// stores and prunes by it) - one home for the pieces both must agree on byte-for-byte.
/// <c>DilosFileConfirm@1</c> receives the finished key opaquely through the data context and never
/// composes one itself.
/// </summary>
internal static class DilosFileFetchCore
{
    /// <summary>Identity of one remote file snapshot whose mtime arrives as text rather than as
    /// a <see cref="DateTime"/> — the shape <c>SftpList@1</c> emits, which <c>DilosFileGate@1</c>
    /// reads after the value has crossed a JSON boundary. The timestamp is taken VERBATIM: it is
    /// the listing's own rendering, and re-parsing and re-formatting it would make the identity
    /// depend on this side's format choice, so an unchanged file could key differently from one
    /// tick to the next and never count as processed.</summary>
    internal static string FileKey(string name, long length, string lastWriteTimeUtc)
    {
        return $"{name}|{length}|{lastWriteTimeUtc}";
    }

    /// <summary>The per-pipeline scope prefix namespacing one fetch scope's keys in the shared
    /// <see cref="Services.DilosFileFetchState"/> singleton, so its keys can never collide with —
    /// or be pruned by - a DIFFERENT pipeline (e.g. ar vs be) sharing the same singleton.
    /// Deliberately built only from the server/directory/pattern triple, not from any global or
    /// tenant identifier: two chains with the same triple are, by definition, the same logical
    /// fetch scope. The gate reads the triple off the listed element, so the values live in one
    /// place - on the listing node - and mean the same thing on both sides.</summary>
    internal static string ScopePrefix(string serverConfiguration, string remoteDirectory, string filePattern)
    {
        return $"{Escape(serverConfiguration)}|{Escape(remoteDirectory)}|{Escape(filePattern)}|";
    }

    /// <summary>'|' separates both the scope components and the <see cref="FileKey"/> fields, and
    /// <see cref="Services.DilosFileFetchState.PruneScopeTo"/> matches scopes by StartsWith — a
    /// literal '|' (or a trailing '\') inside a component must not be able to shift component
    /// boundaries, or two different server/directory/pattern triples could collide on one prefix
    /// and silently prune each other's keys.</summary>
    private static string Escape(string value)
    {
        return value.Replace(@"\", @"\\").Replace("|", @"\|");
    }
}
