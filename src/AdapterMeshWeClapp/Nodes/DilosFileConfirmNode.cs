using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosFileConfirm transform node — the LAST child inside the per-file
/// <c>ForEach@1</c> (<c>iterationPath: $.files</c>) that <c>DilosFileFetchStep@1</c> feeds.
/// </summary>
[NodeName("DilosFileConfirm", 1)]
public record DilosFileConfirmNodeConfiguration : NodeConfiguration
{
    /// <summary>Name of the tenant GlobalConfiguration entry holding the SFTP connection
    /// settings (same entry as the feeding <c>DilosFileFetchStep@1</c>, e.g. "LkvSftp").</summary>
    public required string ServerConfiguration { get; set; }

    /// <summary>Delete the remote file now that its downstream processing succeeded. Must be
    /// configured with the SAME value as the feeding <c>DilosFileFetchStep@1</c>'s
    /// <c>deleteAfterSuccess</c> — the two nodes read/write the same
    /// <see cref="Services.DilosFileFetchState"/> keys for one file element. Default is the
    /// SAFE side (false), matching <see cref="DilosFileFetchTriggerNodeConfiguration.DeleteAfterSuccess"/>.</summary>
    public bool DeleteAfterSuccess { get; set; }

    /// <summary>Data context path of the current file element — the ONE element the enclosing
    /// <c>ForEach@1</c> is iterating. Defaults to <c>$.current</c> (the ForEach <c>keyPath</c>
    /// convention, see the plan's canonical ForEach block) — never <c>$.files</c>, which via
    /// <c>ForEach@1</c>'s parent-fallback read semantics would resolve to the ENTIRE array and
    /// delete files whose iteration has not even run yet.</summary>
    public string Path { get; set; } = "$.current";
}

/// <summary>
/// Confirms successful downstream processing of ONE DILOS file: in keep mode
/// (<c>deleteAfterSuccess=false</c>) marks the file's key as kept on the server so
/// <c>DilosFileFetchStep@1</c> skips it on future ticks instead of re-emitting it; in delete
/// mode removes the file from the LKV SFTP server, marking the key pending BEFORE the delete
/// attempt so a failed delete is retried by <c>DilosFileFetchStep@1</c>'s next listing instead
/// of silently being forgotten. Runs as the LAST child inside the per-file <c>ForEach@1</c> —
/// only reached after every earlier child (the WeClapp write-back) succeeded, exactly where the
/// legacy <see cref="DilosFileFetchTriggerNode"/> deleted after its own
/// <c>ITriggerContext.ExecuteAsync</c> returned successfully. A delete failure (or any other
/// exception here) propagates and aborts the tick — the pending-delete mark already recorded
/// ensures the next cron tick retries the delete via <c>DilosFileFetchStep@1</c>'s listing.
/// Uses the same <c>etlContext.GlobalConfiguration.ResolveSftpSettings</c> +
/// <see cref="ISftpFileSystemFactory"/> seam as <see cref="DilosSftpWriteNode"/>
/// (<c>DilosSftpWriteNode.cs:82</c>).
/// </summary>
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

        // A dry-run confirms nothing: the write nodes upstream skipped their writes, so marking
        // the file kept would make every later REAL tick skip a file that was never delivered,
        // and deleting would consume an LKV file whose content was never written to WeClapp.
        // Input validation and settings resolution still run, so a dry-run surfaces a missing
        // key/path or a half-configured server entry (same contract as DilosSftpWriteNode).
        var isDryRun = nodeContext.PipelineExecutionMode?.IsDryRun == true;

        if (!config.DeleteAfterSuccess)
        {
            var keptFileName = dataContext.Get<string>($"{config.Path}.fileName") ?? key;
            if (isDryRun)
            {
                nodeContext.Info("DilosFileConfirm dry-run: would mark '{0}' kept on server", keptFileName);
                await next(dataContext, nodeContext);
                return;
            }

            state.MarkKeptOnServer(key);
            nodeContext.Info(
                "DilosFileConfirm: '{0}' processed, kept on server (deleteAfterSuccess=false)", keptFileName);
            await next(dataContext, nodeContext);
            return;
        }

        var fullPath = dataContext.Get<string>($"{config.Path}.fullPath");
        if (string.IsNullOrEmpty(fullPath))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileConfirm: no file path found at '{config.Path}.fullPath' — refusing to delete blind");
        }

        var settings = etlContext.GlobalConfiguration.ResolveSftpSettings(config.ServerConfiguration);

        if (isDryRun)
        {
            nodeContext.Info("DilosFileConfirm dry-run: would delete '{0}' after successful processing",
                fullPath);
            await next(dataContext, nodeContext);
            return;
        }

        // Mark BEFORE attempting the delete: if the attempt throws (or the pod dies mid-call),
        // DilosFileFetchStep@1's next listing still finds the key pending and retries the
        // delete — the file is never silently left un-deleted and un-retried.
        state.MarkPendingDelete(key);
        using (var sftp = sftpFileSystemFactory.Connect(settings))
        {
            sftp.DeleteFile(fullPath);
        }

        state.ClearPendingDelete(key);
        nodeContext.Info("DilosFileConfirm: deleted '{0}' after successful processing", fullPath);

        await next(dataContext, nodeContext);
    }
}
