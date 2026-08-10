using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosFileFetch trigger node (AR/BE return path). Polls the LKV SFTP
/// server for DILOS files and starts one pipeline execution per file. There is no built-in
/// SFTP download/trigger node (verified 2026-07-07: only SftpUpload@1 exists; none of the 9
/// trigger nodes touches files) — this is the inbound counterpart to SftpUpload@1.
/// </summary>
[NodeName("DilosFileFetch", 1)]
public record DilosFileFetchTriggerNodeConfiguration : TriggerNodeConfiguration, IDilosFileFetchConfiguration
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

    /// <summary>Seconds between polls (Billbee parity: AR 900 = */15 min, BE 3600 = hourly).</summary>
    public int PollingIntervalSeconds { get; set; } = 900;

    /// <summary>Skip files whose last write is younger than this (partial-file guard DILOS-side;
    /// Billbee lacked one).</summary>
    public int MinFileAgeSeconds { get; set; } = 60;

    /// <summary>Delete the remote file once its pipeline execution succeeded (the normal
    /// consume protocol for go-live). The default is the SAFE side (false): a dry-run
    /// execution succeeds without writing anything to WeClapp, so deleting would consume
    /// the LKV file with no effect — flip to true together with the write node's
    /// <c>dryRun: false</c>. While false, each file is executed once per trigger lifetime
    /// (again after a restart or when the file changes) and left on the server.</summary>
    public bool DeleteAfterSuccess { get; set; }
}

/// <summary>
/// Polls the LKV SFTP server for DILOS AR/BE files and starts one awaited pipeline execution
/// per file as <c>{ "fileName": …, "content": … }</c>. The remote file is deleted only AFTER
/// the pipeline execution for it succeeded (ITriggerContext.ExecuteAsync awaits the full
/// pipeline and rethrows failures) — unlike Billbee, which deleted before processing and relied
/// on a PVC copy. A failed file stays on the server and is retried next poll, so the WeClapp
/// write-back downstream must be idempotent. One bad file does not block the others.
/// With <c>DeleteAfterSuccess=false</c> (dry-run validation: the execution succeeds without
/// writing to WeClapp) files are executed once and left on the server instead.
/// </summary>
[NodeConfiguration(typeof(DilosFileFetchTriggerNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosFileFetchTriggerNode(
    ILogger<DilosFileFetchTriggerNode> logger,
    ISftpFileSystemFactory sftpFileSystemFactory) : ITriggerPipelineNode
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

    // Files that were executed but whose remote delete failed: never execute them again, only
    // retry the delete. Touched only from the sequential polling loop — no locking needed.
    private readonly HashSet<string> _executedButNotDeleted = new();

    // Files already executed in keep mode (DeleteAfterSuccess=false): stay on the server,
    // must not be re-executed every poll. A changed file gets a new key and runs again.
    private readonly HashSet<string> _executedKeptOnServer = new();

    /// <inheritdoc />
    public Task StartAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        context.NodeContext.Info(
            "DilosFileFetch: polling '{0}' for '{1}' every {2}s (deleteAfterSuccess={3})",
            config.RemoteDirectory, config.FilePattern, config.PollingIntervalSeconds,
            config.DeleteAfterSuccess);

        _pollingTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await FetchOnceAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and keep polling — a single failed poll must not kill the trigger.
                    logger.LogError(ex, "DilosFileFetch: poll for '{FilePattern}' failed", config.FilePattern);
                    context.NodeContext.Error("DilosFileFetch poll failed: {0}", ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(ITriggerContext context)
    {
        _cancellationTokenSource?.Cancel();

        if (_pollingTask is { } task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // expected on cancel
            }
        }

        context.NodeContext.Info("DilosFileFetch: polling stopped");
    }

    /// <summary>One poll: list, filter, execute per file, delete after success.
    /// Internal so tests can drive it directly without the polling loop.</summary>
    internal async Task FetchOnceAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>();

        var settings = context.GlobalConfiguration.ResolveSftpSettings(config.ServerConfiguration);

        using var sftp = sftpFileSystemFactory.Connect(settings);

        var files = DilosFileFetchCore.ListMatchingFiles(sftp, config);

        // Forget keys of files that vanished from the server, so the sets stay bounded.
        var currentKeys = files.Select(DilosFileFetchCore.FileKey).ToList();
        _executedButNotDeleted.IntersectWith(currentKeys);
        _executedKeptOnServer.IntersectWith(currentKeys);

        var now = DateTime.UtcNow;

        foreach (var file in files)
        {
            var key = DilosFileFetchCore.FileKey(file);
            try
            {
                // Both memory sets are gated on the CURRENT mode: after a config flip the
                // other mode's stale keys must neither suppress a real write (keep-set
                // surviving a switch to delete mode) nor delete a file in keep mode
                // (stale delete-retry key) — downstream idempotency covers re-executions.
                if (!config.DeleteAfterSuccess && _executedKeptOnServer.Contains(key))
                {
                    continue; // keep mode: already executed, stays on the server unchanged
                }

                if (config.DeleteAfterSuccess && _executedButNotDeleted.Contains(key))
                {
                    // Executed in an earlier poll, only the delete failed — retry just the delete.
                    sftp.DeleteFile(file.FullPath);
                    _executedButNotDeleted.Remove(key);
                    continue;
                }

                if ((now - file.LastWriteTimeUtc).TotalSeconds < config.MinFileAgeSeconds)
                {
                    continue; // possibly still being written — pick it up next poll
                }

                var content = sftp.DownloadText(file.FullPath);
                await context.ExecuteAsync(
                    new ExecutePipelineOptions(DateTime.UtcNow) { ExternalReceivedDateTime = file.LastWriteTimeUtc },
                    new JsonObject { ["fileName"] = file.Name, ["content"] = content });

                if (!config.DeleteAfterSuccess)
                {
                    _executedKeptOnServer.Add(key);
                    context.NodeContext.Info(
                        "DilosFileFetch: '{0}' processed, kept on server (deleteAfterSuccess=false)", file.Name);
                    continue;
                }

                _executedButNotDeleted.Add(key);
                sftp.DeleteFile(file.FullPath);
                _executedButNotDeleted.Remove(key);
            }
            catch (Exception ex)
            {
                // Per-file isolation: the file stays on the server and is retried next poll.
                logger.LogError(ex, "DilosFileFetch: processing '{FileName}' failed", file.Name);
                context.NodeContext.Error("DilosFileFetch: processing '{0}' failed: {1}", file.Name, ex.Message);
            }
        }
    }

}
