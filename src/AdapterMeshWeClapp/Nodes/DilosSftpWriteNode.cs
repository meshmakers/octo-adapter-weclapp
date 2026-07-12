using System.Text;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosSftpWrite node — the ISO-8859-1 delivery counterpart to the
/// built-in SftpUpload@1 (same config surface: serverConfiguration/remoteDirectory/
/// fileNamePath/path), for DILOS files whose golden format is Latin-1, not UTF-8.
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
/// Uploads rendered DILOS file content to the LKV SFTP as ISO-8859-1 bytes. Exists because
/// the golden (DILOS-import-proven) AS/AI files are Latin-1 while the built-in SftpUpload@1
/// writes UTF-8 — with non-ASCII in most articles/customers that corrupts umlauts from day
/// one. Characters outside Latin-1 are replaced with '?' and reported loudly, never silently.
/// </summary>
[NodeConfiguration(typeof(DilosSftpWriteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosSftpWriteNode(
    NodeDelegate next,
    IEtlContext etlContext,
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

        var content = dataContext.Get<string>(config.Path);
        if (string.IsNullOrEmpty(content))
        {
            // An empty DILOS file would be a false snapshot; upstream never emits one
            // (the Batch trigger skips empty polls), so reaching this node empty is a bug.
            throw new WeClappPipelineExecutionException(
                $"No content found at '{config.Path}' — refusing to upload an empty DILOS file");
        }

        if (!etlContext.GlobalConfiguration.IsDefined(config.ServerConfiguration))
        {
            throw new WeClappPipelineExecutionException(
                $"GlobalConfiguration '{config.ServerConfiguration}' is not defined for this " +
                "pipeline — link the configuration entity to the pipeline (Uses association)");
        }

        var remotePath = config.RemoteDirectory.TrimEnd('/') + "/" + fileName;

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.Info("DilosSftpWrite dry-run: would upload '{0}' ({1} chars, ISO-8859-1)",
                remotePath, content.Length);
            await next(dataContext, nodeContext);
            return;
        }

        var bytes = EncodeLatin1(content, remotePath, nodeContext);
        var settings = etlContext.GlobalConfiguration.GetValue<SftpConnectionSettings>(config.ServerConfiguration);

        using (var sftp = sftpFileSystemFactory.Connect(settings))
        {
            sftp.UploadBytes(remotePath, bytes);
        }

        nodeContext.Info("DilosSftpWrite: uploaded '{0}' ({1} bytes, ISO-8859-1)", remotePath, bytes.Length);

        await next(dataContext, nodeContext);
    }

    /// <summary>ISO-8859-1 covers exactly U+0000–U+00FF; anything above is replaced with '?'
    /// and reported — silent corruption is the failure mode this node exists to prevent.</summary>
    private static byte[] EncodeLatin1(string content, string remotePath, INodeContext nodeContext)
    {
        var offenders = content.Where(ch => ch > 'ÿ').ToArray();
        if (offenders.Length > 0)
        {
            var distinct = string.Join(" ", offenders.Distinct().Select(o => $"U+{(int)o:X4}"));
            nodeContext.Warning(
                "DilosSftpWrite: {0} non-ISO-8859-1 character(s) in '{1}' replaced with '?' (distinct: {2})",
                offenders.Length, remotePath, distinct);
        }

        var latin1 = Encoding.GetEncoding("ISO-8859-1",
            new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        return latin1.GetBytes(content);
    }
}
