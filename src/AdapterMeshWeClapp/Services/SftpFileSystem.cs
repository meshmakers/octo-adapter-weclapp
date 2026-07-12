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
