using System.Text.RegularExpressions;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration surface <see cref="DilosFileFetchCore"/> needs to identify and list one DILOS
/// fetch scope on the LKV SFTP server — implemented by both
/// <see cref="DilosFileFetchTriggerNodeConfiguration"/> (the legacy polling trigger) and
/// <see cref="DilosFileFetchStepNodeConfiguration"/> (the cron-trigger-redesign step node,
/// AB#4228/G2), so the two node families share one listing/key implementation without either
/// depending on the other's configuration type. Counterpart of
/// <see cref="IWeClappFetchConfiguration"/> on the WeClapp ingestion side.
/// </summary>
internal interface IDilosFileFetchConfiguration
{
    /// <summary>Name of the tenant GlobalConfiguration entry holding the SFTP connection settings.</summary>
    string ServerConfiguration { get; }

    /// <summary>Remote directory to list.</summary>
    string RemoteDirectory { get; }

    /// <summary>Case-insensitive glob (Billbee semantics), e.g. "AR*TXT" or "BE*txt".</summary>
    string FilePattern { get; }
}

/// <summary>
/// DILOS SFTP listing and key/scope format shared by <see cref="DilosFileFetchTriggerNode"/>
/// (legacy polling trigger), <see cref="DilosFileFetchStepNode"/> (cron-trigger-redesign step
/// node, AB#4228/G2) and the <see cref="Services.DilosFileFetchState"/> key contract — one home
/// for the pieces both node families must agree on byte-for-byte. The per-file loops themselves
/// stay node-specific: the trigger executes each file inline and deletes right after its own
/// <c>ITriggerContext.ExecuteAsync</c>, while the step only emits <c>$.files</c> and defers
/// first-time keep/delete bookkeeping to <c>DilosFileConfirm@1</c>. Counterpart of
/// <see cref="WeClappFetchCore"/> on the WeClapp ingestion side; lives in its own file so it
/// survives the legacy trigger's removal once every environment runs the passive-trigger YAMLs.
/// </summary>
internal static class DilosFileFetchCore
{
    /// <summary>Billbee-compatible glob: '*' → any run, '?' → one char, anchored, case-insensitive.</summary>
    internal static bool GlobMatch(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>Lists <see cref="IDilosFileFetchConfiguration.RemoteDirectory"/> and returns the
    /// non-directory entries matching <see cref="IDilosFileFetchConfiguration.FilePattern"/> in
    /// Ordinal name order — the shared listing surface of both fetch nodes.</summary>
    internal static List<SftpFileEntry> ListMatchingFiles(ISftpFileSystem sftp, IDilosFileFetchConfiguration config)
    {
        return sftp.ListFiles(config.RemoteDirectory)
            .Where(f => !f.IsDirectory && GlobMatch(f.Name, config.FilePattern))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Identity of one remote file snapshot: name, size and mtime — a file rewritten
    /// under the same name gets a new key and is treated as new.</summary>
    internal static string FileKey(SftpFileEntry file)
    {
        return $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>The per-pipeline scope prefix namespacing a step's keys in the shared
    /// <see cref="Services.DilosFileFetchState"/> singleton, so its keys can never collide with —
    /// or be pruned by — a DIFFERENT pipeline's <c>DilosFileFetchStep@1</c> (e.g. ar vs be)
    /// sharing the same singleton. Deliberately built only from the step's own config, not from
    /// any global or tenant identifier: two chains with the same server/directory/pattern are,
    /// by definition, the same logical fetch scope.</summary>
    internal static string ScopePrefix(IDilosFileFetchConfiguration config)
    {
        return $"{config.ServerConfiguration}|{config.RemoteDirectory}|{config.FilePattern}|";
    }
}
