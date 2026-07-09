using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public record DilosFileFetchTriggerNodeConfiguration : TriggerNodeConfiguration
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
}

/// <summary>
/// Polls the LKV SFTP server for DILOS AR/BE files and starts one awaited pipeline execution
/// per file as <c>{ "fileName": …, "content": … }</c>. The remote file is deleted only AFTER
/// the pipeline execution for it succeeded (ITriggerContext.ExecuteAsync awaits the full
/// pipeline and rethrows failures) — unlike Billbee, which deleted before processing and relied
/// on a PVC copy. A failed file stays on the server and is retried next poll, so the WeClapp
/// write-back downstream must be idempotent. One bad file does not block the others.
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

    /// <inheritdoc />
    public Task StartAsync(ITriggerContext context)
    {
        var config = context.NodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        context.NodeContext.Info(
            "DilosFileFetch: polling '{0}' for '{1}' every {2}s",
            config.RemoteDirectory, config.FilePattern, config.PollingIntervalSeconds);

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

        if (!context.GlobalConfiguration.IsDefined(config.ServerConfiguration))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileFetch: global configuration '{config.ServerConfiguration}' is not defined for the tenant");
        }

        var settings = context.GlobalConfiguration.GetValue<SftpConnectionSettings>(config.ServerConfiguration);
        if (string.IsNullOrWhiteSpace(settings.Password) && string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileFetch: global configuration '{config.ServerConfiguration}' has neither password nor private key");
        }

        using var sftp = sftpFileSystemFactory.Connect(settings);

        var files = sftp.ListFiles(config.RemoteDirectory)
            .Where(f => !f.IsDirectory && GlobMatch(f.Name, config.FilePattern))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        // Forget keys of files that vanished from the server, so the set stays bounded.
        _executedButNotDeleted.IntersectWith(files.Select(FileKey));

        var now = DateTime.UtcNow;

        foreach (var file in files)
        {
            var key = FileKey(file);
            try
            {
                if (_executedButNotDeleted.Contains(key))
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

    private static string FileKey(SftpFileEntry file) =>
        $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    /// <summary>Billbee-compatible glob: '*' → any run, '?' → one char, anchored, case-insensitive.</summary>
    internal static bool GlobMatch(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }
}
