using System.Text;
using Lkv.WeClapp.Core.Dilos;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosSftpWrite node — the ISO-8859-1 delivery counterpart to the
/// built-in SftpUpload@1 (same config surface: serverConfiguration/remoteDirectory/
/// fileNamePath/path), for DILOS files whose golden format is Latin-1.
/// </summary>
[NodeName("DilosSftpWrite", 1)]
public record DilosSftpWriteNodeConfiguration : PathNodeConfiguration
{
    /// <summary>Name of the tenant GlobalConfiguration entry with the SFTP connection
    /// settings (e.g. "LkvSftp" — shared with DilosFileFetch@1).</summary>
    public required string ServerConfiguration { get; set; }

    /// <summary>Remote target directory (default "/", the Billbee production layout).</summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>JSONPath to the file name (produced by DilosRender's
    /// <c>fileNameTargetPath</c>).</summary>
    public required string FileNamePath { get; set; }
}

/// <summary>
/// Uploads rendered DILOS file content to the LKV SFTP as ISO-8859-1 bytes
/// (<see cref="DilosFile.Encoding"/>). It existed because the golden (DILOS-import-proven)
/// AS/AI files are Latin-1 while SftpUpload@1 could only write UTF-8 — with non-ASCII in
/// most articles/customers that corrupted umlauts from day one. SftpUpload@1 takes an
/// encoding since r3.4.89 and the shipped pipelines deliver through it, so no pipeline
/// references this node any more; it stays registered until the removal train. Characters
/// outside Latin-1 are replaced with '?' and reported loudly (also in dry-run), never silently.
/// </summary>
[NodeConfiguration(typeof(DilosSftpWriteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosSftpWriteNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpFileSystemFactory sftpFileSystemFactory) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosSftpWriteNodeConfiguration>();

        var fileName = dataContext.Get<string>(config.FileNamePath);
        if (string.IsNullOrEmpty(fileName))
        {
            throw new WeClappPipelineExecutionException(
                $"No file name found at '{config.FileNamePath}' — refusing to upload a nameless DILOS file");
        }

        // A legitimate DILOS name never contains a path separator or dot segment — reject
        // instead of resolving, so a poisoned upstream value cannot escape remoteDirectory.
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
        {
            throw new WeClappPipelineExecutionException(
                $"File name '{fileName}' contains a path separator or dot segment — refusing to upload");
        }

        var content = dataContext.Get<string>(config.Path);
        if (string.IsNullOrEmpty(content))
        {
            // An empty DILOS file would be a false snapshot; upstream never emits one
            // (the Batch trigger skips empty polls and DilosRender ends the pipeline when
            // a batch has no deliverable articles), so reaching this node empty is a bug.
            throw new WeClappPipelineExecutionException(
                $"No content found at '{config.Path}' — refusing to upload an empty DILOS file");
        }

        var remotePath = config.RemoteDirectory.TrimEnd('/') + "/" + fileName;

        // Encode (non-Latin-1 detection) and resolve the SFTP settings BEFORE the dry-run
        // gate: the dry-run is the pre-go-live validation pass and must surface encoding
        // loss and a half-configured LkvSftp entry too — only the network upload is skipped.
        // Encode first so a config error still leaves the encoding warning in the log.
        var bytes = EncodeLatin1(content, remotePath, nodeContext);
        var settings = etlContext.GlobalConfiguration.ResolveSftpSettings(config.ServerConfiguration);

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.Info("DilosSftpWrite dry-run: would upload '{0}' ({1} bytes, ISO-8859-1)",
                remotePath, bytes.Length);
            await next(dataContext, nodeContext);
            return;
        }

        using (var sftp = sftpFileSystemFactory.Connect(settings))
        {
            sftp.UploadBytes(remotePath, bytes);
        }

        nodeContext.Info("DilosSftpWrite: uploaded '{0}' ({1} bytes, ISO-8859-1)", remotePath, bytes.Length);

        await next(dataContext, nodeContext);
    }

    /// <summary>ISO-8859-1 covers exactly U+0000–U+00FF; anything above becomes ONE '?' per
    /// Unicode scalar (a surrogate pair counts once — the framework fallback would emit two)
    /// and is reported loudly — silent corruption is the failure mode this node exists to
    /// prevent. Clean content takes the fast path without any extra allocation.</summary>
    private static byte[] EncodeLatin1(string content, string remotePath, INodeContext nodeContext)
    {
        var replaced = 0;
        HashSet<string>? offenders = null;
        StringBuilder? sanitized = null;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch <= 'ÿ')
            {
                sanitized?.Append(ch);
                continue;
            }

            replaced++;
            offenders ??= new HashSet<string>();
            sanitized ??= new StringBuilder(content.Length).Append(content, 0, i);
            sanitized.Append('?');
            if (char.IsHighSurrogate(ch) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
            {
                offenders.Add($"U+{char.ConvertToUtf32(ch, content[i + 1]):X4}");
                i++; // the pair is ONE scalar → ONE '?'
            }
            else
            {
                offenders.Add($"U+{(int)ch:X4}");
            }
        }

        if (replaced > 0)
        {
            nodeContext.Warning(
                "DilosSftpWrite: {0} non-ISO-8859-1 character(s) in '{1}' replaced with '?' (distinct: {2})",
                replaced, remotePath, string.Join(" ", offenders!));
        }

        return DilosFile.Encoding.GetBytes(sanitized?.ToString() ?? content);
    }
}
