using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosFileFetchStep transform node — the step-node counterpart of
/// <see cref="DilosFileFetchTriggerNodeConfiguration"/> for the cron-trigger redesign
/// (AB#4228/G2): same fetch surface, minus the polling-only field
/// (<c>PollingIntervalSeconds</c>) that makes no sense once a platform cron trigger
/// (<c>FromPipelineTriggerEvent@1</c>) drives execution instead of a poll loop.
/// </summary>
[NodeName("DilosFileFetchStep", 1)]
public record DilosFileFetchStepNodeConfiguration : NodeConfiguration, IDilosFileFetchConfiguration
{
    /// <summary>Name of the tenant GlobalConfiguration entry holding the SFTP connection
    /// settings (same JSON shape as SftpUpload@1, e.g. "LkvSftp" shared for both directions).</summary>
    public required string ServerConfiguration { get; set; }

    /// <summary>Remote directory to poll. Billbee production effectively used "/" — whether the
    /// WeClapp mandate gets per-type subdirectories is an open LKV question.</summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>Case-insensitive glob (Billbee semantics: '*' any run, '?' one char, anchored),
    /// e.g. "AR*TXT" or "BE*txt" — matches golden names AR00006946.TXT / BE_20240205035403463.txt.</summary>
    public required string FilePattern { get; set; }

    /// <summary>Skip files whose last write is younger than this (partial-file guard DILOS-side;
    /// Billbee lacked one).</summary>
    public int MinFileAgeSeconds { get; set; } = 60;

    /// <summary>Delete the remote file once its downstream processing succeeded. Read by BOTH
    /// this step (gates the keep-mode skip / pending-delete retry) and <c>DilosFileConfirm@1</c>
    /// (performs the actual first-time delete) — the two must be configured with the SAME
    /// value for one pipeline. The default is the SAFE side (false), matching
    /// <see cref="DilosFileFetchTriggerNodeConfiguration.DeleteAfterSuccess"/>.</summary>
    public bool DeleteAfterSuccess { get; set; }
}

/// <summary>
/// Lists the LKV SFTP server for DILOS files and seeds <c>$.files</c> with one element per
/// matching, ready file — the step-node counterpart of <see cref="DilosFileFetchTriggerNode"/>
/// for the cron-trigger redesign (AB#4228/G2): a downstream <c>ForEach@1</c>
/// (<c>iterationPath: $.files</c>) drives the per-file write-back chain instead of this node
/// calling <c>ITriggerContext.ExecuteAsync</c> itself. <c>$.files</c> is ALWAYS seeded, even
/// empty — a missing/non-array <c>iterationPath</c> aborts a downstream <c>ForEach@1</c> with
/// <c>PathMustBeArray</c>; an empty array no-ops.
/// <para/>
/// <b>Delete split:</b> unlike the trigger (which deletes right after its own
/// <c>ExecuteAsync</c> succeeds), this step never deletes a freshly emitted file — first-time
/// deletion after successful downstream processing belongs exclusively to
/// <c>DilosFileConfirm@1</c>, the last child of the downstream <c>ForEach@1</c>. This step DOES
/// retry a delete that <c>DilosFileConfirm@1</c> attempted in an earlier tick and failed
/// (<see cref="DilosFileFetchState.HasPendingDelete"/>) — that file is deleted right here during
/// listing, WITHOUT being emitted or re-executed, mirroring
/// <see cref="DilosFileFetchTriggerNode"/>'s own delete-retry in its
/// <c>FetchOnceAsync</c>. Cross-tick memory of both kinds lives in the
/// injected <see cref="DilosFileFetchState"/> DI singleton, not on this node instance — the
/// pipeline engine constructs a fresh node per chain (per tick), so instance fields would lose
/// their state between ticks. A pod restart clears the singleton exactly like it used to clear
/// the trigger's instance fields: a kept file is re-emitted and re-executed once more, relying
/// on downstream idempotency — identical to today's restart behavior.
/// <para/>
/// The SAME singleton instance is shared by every pipeline wired to this node (e.g. ar AND be),
/// so every key this step reads or writes is namespaced with a scope prefix built from its own
/// config (<see cref="DilosFileFetchCore.ScopePrefix"/>) — without it, one pipeline's tick would
/// prune another pipeline's keys via <see cref="DilosFileFetchState.PruneScopeTo"/>.
/// </summary>
[NodeConfiguration(typeof(DilosFileFetchStepNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosFileFetchStepNode(
    NodeDelegate next,
    ILogger<DilosFileFetchStepNode> logger,
    IMeshEtlContext etlContext,
    ISftpFileSystemFactory sftpFileSystemFactory,
    DilosFileFetchState state) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosFileFetchStepNodeConfiguration>();

        var settings = etlContext.GlobalConfiguration.ResolveSftpSettings(config.ServerConfiguration);

        using var sftp = sftpFileSystemFactory.Connect(settings);

        var files = DilosFileFetchCore.ListMatchingFiles(sftp, config);

        var scopePrefix = DilosFileFetchCore.ScopePrefix(config);

        // Forget THIS scope's keys for files that vanished from the server, so the singleton
        // stays bounded without touching another pipeline's keys sharing the same singleton
        // (the DI singleton is shared across every pipeline wired to DilosFileFetchStep@1 —
        // see DilosFileFetchState's class summary).
        state.PruneScopeTo(scopePrefix, files.Select(f => scopePrefix + DilosFileFetchCore.FileKey(f)));

        var now = DateTime.UtcNow;
        var emitted = new JsonArray();

        foreach (var file in files)
        {
            var key = scopePrefix + DilosFileFetchCore.FileKey(file);
            try
            {
                // Both checks are gated on the CURRENT mode: after a config flip a stale key
                // from the OTHER mode must neither suppress a real emission (a keep-mode key
                // surviving a switch to delete mode) nor delete a file in keep mode (a stale
                // delete-retry key) — downstream idempotency covers re-executions, exactly like
                // the trigger this replaces.
                if (!config.DeleteAfterSuccess && state.WasKeptOnServer(key))
                {
                    continue; // keep mode: already confirmed, stays on the server unchanged
                }

                if (config.DeleteAfterSuccess && state.HasPendingDelete(key))
                {
                    // DilosFileConfirm@1 processed this file in an earlier tick; only its
                    // delete failed — retry just the delete, never re-emit/re-execute the file.
                    sftp.DeleteFile(file.FullPath);
                    state.ClearPendingDelete(key);
                    continue;
                }

                if ((now - file.LastWriteTimeUtc).TotalSeconds < config.MinFileAgeSeconds)
                {
                    continue; // possibly still being written — pick it up next tick
                }

                var content = sftp.DownloadText(file.FullPath);
                emitted.Add(new JsonObject
                {
                    ["fileName"] = file.Name,
                    ["content"] = content,
                    ["fullPath"] = file.FullPath,
                    ["key"] = key,
                    ["lastWriteTimeUtc"] = file.LastWriteTimeUtc,
                });
            }
            catch (Exception ex)
            {
                // Per-file isolation during listing (a corrupt file, a permission glitch on ONE
                // remote entry, or a failed retry-delete) must not stop the others from being
                // listed and emitted — the file stays on the server and is retried next tick,
                // exactly like the trigger this replaces.
                logger.LogError(ex, "DilosFileFetchStep: listing '{FileName}' failed", file.Name);
                nodeContext.Error("DilosFileFetchStep: listing '{0}' failed: {1}", file.Name, ex.Message);
            }
        }

        dataContext.Set("$.files", emitted, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        logger.LogDebug("DilosFileFetchStep: emitted {Count} of {Total} listed file(s) matching '{Pattern}'",
            emitted.Count, files.Count, config.FilePattern);

        await next(dataContext, nodeContext);
    }
}
