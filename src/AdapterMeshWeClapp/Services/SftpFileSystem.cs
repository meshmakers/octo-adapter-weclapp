namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// SFTP connection settings resolved from a tenant GlobalConfiguration entry. The JSON shape is
/// identical to the (private) server configuration record of the built-in SftpUpload@1 node, so
/// one tenant entry (e.g. "LkvSftp") serves both the outbound AS/AI upload and the inbound
/// DILOS AR/BE fetch — credentials live in tenant configuration, never in pipeline YAML.
/// </summary>
public record SftpConnectionSettings
{
    /// <summary>SFTP host name.</summary>
    public required string Host { get; init; }

    /// <summary>SFTP port.</summary>
    public int Port { get; init; } = 22;

    /// <summary>Login user name.</summary>
    public required string Username { get; init; }

    /// <summary>Password for password authentication (either this or <see cref="PrivateKey"/>).</summary>
    public string? Password { get; init; }

    /// <summary>PEM private key content for key authentication.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>Optional passphrase for <see cref="PrivateKey"/>.</summary>
    public string? PrivateKeyPassphrase { get; init; }
}

/// <summary>A remote file listing entry (the subset of SSH.NET's ISftpFile the fetch trigger needs).</summary>
public record SftpFileEntry(string Name, string FullPath, bool IsDirectory, DateTime LastWriteTimeUtc, long Length);

/// <summary>
/// Thin seam over an open SFTP session so nodes stay unit-testable (the IHttpClientFactory
/// analogue for SFTP); the production implementation wraps SSH.NET.
/// </summary>
public interface ISftpFileSystem : IDisposable
{
    /// <summary>Lists the entries of a remote directory (files and directories).</summary>
    IReadOnlyList<SftpFileEntry> ListFiles(string directory);

    /// <summary>Downloads a remote file as text (DILOS AR/BE files are pure ASCII — golden-verified).</summary>
    string DownloadText(string fullPath);

    /// <summary>Uploads raw bytes to a remote path, overwriting an existing file — encoding
    /// is the CALLER's contract (DILOS delivery writes ISO-8859-1).</summary>
    void UploadBytes(string fullPath, byte[] content);

    /// <summary>Deletes a remote file.</summary>
    void DeleteFile(string fullPath);
}

/// <summary>Creates connected SFTP sessions from tenant-configured connection settings.</summary>
public interface ISftpFileSystemFactory
{
    /// <summary>Opens a connected session; the caller disposes it after the poll.</summary>
    ISftpFileSystem Connect(SftpConnectionSettings settings);
}

/// <summary>
/// Shared resolution of a tenant SFTP GlobalConfiguration entry — one validation for both
/// transfer directions (DilosFileFetch@1 and DilosSftpWrite@1), so a half-configured entry
/// fails with the same clear message everywhere instead of reaching SSH.NET with empty
/// credentials.
/// </summary>
public static class SftpConnectionSettingsResolver
{
    public static SftpConnectionSettings ResolveSftpSettings(
        this Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.IGlobalConfiguration globalConfiguration,
        string serverConfiguration)
    {
        if (!globalConfiguration.IsDefined(serverConfiguration))
        {
            throw new WeClappPipelineExecutionException(
                $"Global configuration '{serverConfiguration}' is not defined for this pipeline " +
                "— link the configuration entity to the pipeline (Uses association)");
        }

        var settings = globalConfiguration.GetValue<SftpConnectionSettings>(serverConfiguration);
        if (string.IsNullOrWhiteSpace(settings.Password) && string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            throw new WeClappPipelineExecutionException(
                $"Global configuration '{serverConfiguration}' has neither password nor private key");
        }

        return settings;
    }
}
