using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosFileConfirm transform node — the LAST child inside the per-file
/// <c>ForEach@1</c> that <c>DilosFileGate@1</c> feeds.
/// </summary>
[NodeName("DilosFileConfirm", 1)]
public record DilosFileConfirmNodeConfiguration : NodeConfiguration
{
    /// <summary>Data context path of the current file element — the ONE element the enclosing
    /// <c>ForEach@1</c> is iterating. Defaults to <c>$.current</c> (the ForEach <c>keyPath</c>
    /// convention) — never the array itself, which via <c>ForEach@1</c>'s parent-fallback read
    /// semantics would resolve to EVERY element and confirm files whose iteration has not run.
    /// <para/>
    /// There is deliberately nothing else to configure here: the keep/delete mode and the server
    /// to delete from are read from the element, which <c>DilosFileGate@1</c> stamped. They used
    /// to be repeated on this node, and the two copies had to agree — a mismatch reprocessed
    /// every file forever or deleted files nothing had written. Both values live in tenant-side
    /// pipeline definitions and are editable in the Studio, so that was one click away.</summary>
    public string Path { get; set; } = "$.current";
}

/// <summary>
/// Confirms successful downstream processing of ONE DILOS file: in keep mode it marks the file's
/// key as kept on the server, so <c>DilosFileGate@1</c> drops it on later ticks instead of
/// letting it through again; in delete mode it removes the file from the LKV SFTP server,
/// marking the key pending BEFORE the attempt so a failed delete is retried by the gate's next
/// run rather than silently forgotten. Runs as the LAST child inside the per-file
/// <c>ForEach@1</c> — only reached once every earlier child (the WeClapp write-back) succeeded.
/// A delete failure, like any other exception here, propagates and fails this iteration; the
/// pending mark already recorded is what makes the next tick retry the delete.
/// <para/>
/// The mode and the server come from the element rather than from this node's configuration, so
/// this node and the gate can no longer be configured to disagree.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context carrying the tenant global configuration</param>
/// <param name="sftpFileSystemFactory">Opens the SFTP session a delete needs</param>
/// <param name="state">Cross-tick memory shared with <c>DilosFileGate@1</c></param>
[NodeConfiguration(typeof(DilosFileConfirmNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosFileConfirmNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpFileSystemFactory sftpFileSystemFactory,
    DilosFileFetchState state) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosFileConfirmNodeConfiguration>();

        var key = dataContext.Get<string>($"{config.Path}.key");
        if (string.IsNullOrEmpty(key))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileConfirm: no file key found at '{config.Path}.key' — refusing to confirm without a key");
        }

        // Never defaulted: assuming keep would deliver the same file again on every tick,
        // assuming delete would consume an LKV file on the word of a stamp that is not there.
        var deleteAfterSuccess = RequiredStamp(dataContext, config.Path, "deleteAfterSuccess",
            path => dataContext.Get<bool>(path));

        // A dry-run confirms nothing: the write nodes upstream skipped their writes, so marking
        // the file kept would make every later REAL tick skip a file that was never delivered,
        // and deleting would consume an LKV file whose content never reached WeClapp. Input
        // validation still runs, so a dry-run surfaces a missing stamp or a half-configured
        // server entry (same contract as DilosFileGate@1).
        var isDryRun = nodeContext.PipelineExecutionMode?.IsDryRun == true;

        if (!deleteAfterSuccess)
        {
            var keptFileName = dataContext.Get<string>($"{config.Path}.name") ?? key;
            if (isDryRun)
            {
                nodeContext.Info("DilosFileConfirm dry-run: would mark '{0}' kept on server", keptFileName);
                await next(dataContext, nodeContext);
                return;
            }

            state.MarkKeptOnServer(key);
            nodeContext.Info("DilosFileConfirm: '{0}' processed, kept on server (keep mode)", keptFileName);
            await next(dataContext, nodeContext);
            return;
        }

        var fullPath = dataContext.Get<string>($"{config.Path}.fullPath");
        if (string.IsNullOrEmpty(fullPath))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileConfirm: no file path found at '{config.Path}.fullPath' — refusing to delete blind");
        }

        var serverConfiguration = RequiredStamp(dataContext, config.Path, "serverConfiguration",
            path => dataContext.Get<string>(path) ?? "");
        var settings = etlContext.GlobalConfiguration.ResolveSftpSettings(serverConfiguration);

        if (isDryRun)
        {
            nodeContext.Info("DilosFileConfirm dry-run: would delete '{0}' after successful processing", fullPath);
            await next(dataContext, nodeContext);
            return;
        }

        // Mark BEFORE attempting the delete: if the attempt throws (or the pod dies mid-call),
        // DilosFileGate@1's next run still finds the key pending and retries the delete — the
        // file is never left both un-deleted and un-retried.
        state.MarkPendingDelete(key);
        using (var sftp = sftpFileSystemFactory.Connect(settings))
        {
            sftp.DeleteFile(fullPath);
        }

        state.ClearPendingDelete(key);
        nodeContext.Info("DilosFileConfirm: deleted '{0}' after successful processing", fullPath);

        await next(dataContext, nodeContext);
    }

    /// <summary>Reads one value <c>DilosFileGate@1</c> stamps into every element it lets through.
    /// Absence is an error rather than a default: these two values exist to remove a choice from
    /// the configuration, and quietly inventing one here would put it back.</summary>
    private static T RequiredStamp<T>(IDataContext dataContext, string elementPath, string stamp,
        Func<string, T> read)
    {
        var path = $"{elementPath}.{stamp}";
        if (!dataContext.Exists(path))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileConfirm: no '{stamp}' stamp found at '{path}' — the element must come " +
                "from DilosFileGate@1, which is where that value is configured");
        }

        return read(path);
    }
}
