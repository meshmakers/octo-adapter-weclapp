using System.Text;
using Renci.SshNet;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// Production <see cref="ISftpFileSystemFactory"/> backed by SSH.NET — client creation follows
/// the built-in SftpUploadNode (password or in-memory PEM private key).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class SshNetSftpFileSystemFactory : ISftpFileSystemFactory
{
    /// <inheritdoc />
    public ISftpFileSystem Connect(SftpConnectionSettings settings)
    {
        var client = CreateSftpClient(settings);
        try
        {
            client.Connect();
        }
        catch
        {
            // The disposing wrapper only exists after a successful connect — a failed
            // Connect() (unreachable host, bad credentials) must not leak the client
            // and its socket on every poll.
            client.Dispose();
            throw;
        }

        return new SshNetSftpFileSystem(client);
    }

    private static SftpClient CreateSftpClient(SftpConnectionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey));
            var privateKeyFile = string.IsNullOrWhiteSpace(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);

            return new SftpClient(settings.Host, settings.Port, settings.Username, [privateKeyFile]);
        }

        return new SftpClient(settings.Host, settings.Port, settings.Username, settings.Password ?? string.Empty);
    }
}

/// <summary>An open SSH.NET SFTP session; disposed after each poll.</summary>
internal sealed class SshNetSftpFileSystem(SftpClient client) : ISftpFileSystem
{
    public IReadOnlyList<SftpFileEntry> ListFiles(string directory)
    {
        return client.ListDirectory(directory)
            .Select(f => new SftpFileEntry(f.Name, f.FullName, f.IsDirectory, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }

    public string DownloadText(string fullPath)
    {
        using var stream = new MemoryStream();
        client.DownloadFile(fullPath, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void DeleteFile(string fullPath)
    {
        client.DeleteFile(fullPath);
    }

    public void Dispose()
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch
        {
            // Cleanup must not throw: a half-dead session failing Disconnect() (or a second
            // Dispose reaching the already-disposed client) still needs the Dispose below.
        }
        finally
        {
            client.Dispose();
        }
    }
}
