# SFTP standard nodes (product PR) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `SftpList@1` and `SftpDownload@1` to the product node library, on a shared SFTP connection layer that also gives `SftpUpload@1` optional host key pinning.

**Architecture:** A connection seam (`ISftpSessionFactory` / `ISftpSession`) owns settings resolution, the per-server concurrency limit, client creation and host key verification. All three SFTP nodes sit on it and stay thin. `SftpList@1` emits metadata only; `SftpDownload@1` fetches exactly one file, mirroring `SftpUpload@1`. No delete, no cross-tick state, no DILOS knowledge enters the product.

**Tech Stack:** .NET 10, SSH.NET 2026.0.0, xunit 2.9.3, FakeItEasy 9.0.1.

**Spec:** `docs/superpowers/specs/2026-08-20-sftp-standard-nodes-design.md` (in `octo-adapter-weclapp`, alongside this plan)

**Repository under change:** `octo-mesh-adapter` (all paths below are relative to that repo). This plan and the spec live in `octo-adapter-weclapp` because that is where the LKV work is documented; the product PR itself carries only code and product docs.

**Scope note:** the spec describes two PRs. This plan covers the first one. The adapter-side plan (gate node, confirm node, AR/BE YAML) is written separately, after this PR is released, because it depends on the released package version and the registered node names. That plan rewrites pipeline YAML, so it should be written with the `octo-claude-skills:pipeline-expert` skill; this one does not need it, being node implementation rather than pipeline authoring.

## Global Constraints

- Solution: `Octo.MeshAdapter.sln`, target framework `net10.0`.
- Build and test with the **default Debug configuration**. `-c DebugL` resolves Octo packages from the local `../nuget` feed, which lags behind the published packages.
- Node names are decided and must not be revisited in the PR: `SftpList@1`, `SftpDownload@1`.
- Every new node needs both a `CLAUDE.md` entry and a `docs/developer-guide.md` section. That is the house convention in this repo.
- Host key fingerprints are SHA-256, non-padded base64, exactly what `ssh-keygen -lf` prints; a leading `SHA256:` is accepted and stripped.
- `lastWriteTimeUtc` is emitted with the round-trip format specifier `"O"` and `CultureInfo.InvariantCulture`. A consumer builds a file identity from this string, so the representation must be stable listing over listing.
- Encoding names are resolved through the existing `SftpUploadEncoding.Resolve`.
- Documentation, code comments and the PR body are English. The PR body uses plain hyphens only, never en dashes or em dashes. The `CLAUDE.md` snippets in Task 9 deliberately keep the em dashes that file uses throughout; that rule is about the PR body, not about matching a file's own style.
- Existing behaviour of `SftpUpload@1` must not change, except that host key pinning becomes available.
- `MeshAdapterPipelineExecutionException` is `internal` with private constructors: errors are raised through static factory methods that format their own message, node-scoped ones as `$"[{nodeContext.NodePath}]: ..."`. The test project reaches the type through `InternalsVisibleTo`, which is how the existing upload tests assert on it.

---

## File Structure

**New, shared SFTP layer**, flat in `src/MeshAdapter.Sdk/Nodes/`, namespace `Meshmakers.Octo.Sdk.MeshAdapter.Nodes`. Not in a category folder, because the layer serves an extract node and a load node alike; flat rather than in a new subfolder, because that is where the repository already keeps helpers of exactly this kind - `StreamDataGapAnalyzer`, `StreamDataGapScanner`, `StreamDataNodeHelpers`, `Query` and `FieldFilterExtensions`:

- `SftpServerSettings.cs` - the tenant GlobalConfiguration entry shape, including the new `HostKeyFingerprint`
- `SftpServerSettingsResolver.cs` - resolves and validates that entry, one place for all three nodes
- `SftpHostKeyVerifier.cs` - pure fingerprint comparison, unit-testable without a server
- `SftpEntry.cs`, `ISftpSession.cs`, `ISftpSessionFactory.cs` - the seam
- `SshNetSftpSessionFactory.cs` - the SSH.NET implementation: semaphore, client creation, pinning

**New node configurations** (`src/MeshNodes.Sdk/Extract/`, namespace `Meshmakers.Octo.MeshAdapter.Nodes.Extract`):

- `SftpListNodeConfiguration.cs`, `SftpDownloadNodeConfiguration.cs`

**New node implementations** (`src/MeshAdapter.Sdk/Nodes/Extract/`, namespace `Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract`):

- `SftpListNode.cs`, `SftpDownloadNode.cs`
- `SftpFileNameGlob.cs` - the glob used by the list node
- `SftpContentDecoder.cs` - counterpart of `Nodes/Load/SftpContentEncoder.cs`

**Modified:**

- `src/MeshAdapter.Sdk/Nodes/Load/SftpUploadNode.cs` - moves onto the seam
- `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs` - new factory methods
- `src/MeshNodes.Sdk/Configuration/DataPipelineBuilderExtensions.cs` - register the two configurations
- `src/MeshAdapter.Sdk/Configuration/DependencyInjection/ServiceCollectionExtensions.cs` - register the factory
- `CLAUDE.md`, `docs/developer-guide.md`

---

### Task 1: SFTP server settings and their resolution

Pulls the settings record and its two validations out of `SftpUploadNode`, so the list and download nodes do not copy them, and adds the pinning field.

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/SftpServerSettings.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/SftpServerSettingsResolver.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/SftpServerSettingsResolverTests.cs`

**Interfaces:**
- Consumes: `IMeshEtlContext.GlobalConfiguration`, `MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(INodeContext, string, string)`, `MeshAdapterPipelineExecutionException.SftpAuthNotConfigured(INodeContext)`
- Produces: `public sealed record SftpServerSettings`; `public static SftpServerSettings SftpServerSettingsResolver.Resolve(IMeshEtlContext etlContext, string serverConfigurationName, INodeContext nodeContext)`

- [x] **Step 1: Write the failing test**

```csharp
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class SftpServerSettingsResolverTests : NodeTestBase
{
    private const string ServerConfig = "sftp-server-1";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    public SftpServerSettingsResolverTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private INodeContext NodeContext()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = "/out",
            FileName = "x.txt",
            Path = "$.content"
        };
        var (_, nodeContext, _) = PrepareTest<SftpUploadNodeConfiguration>(config);
        return nodeContext;
    }

    [Fact]
    public void Resolve_EntryNotDefined_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(false);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
    }

    [Fact]
    public void Resolve_NeitherPasswordNorPrivateKey_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user" });

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext()));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_PasswordConfigured_ReturnsSettingsWithDefaults()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });

        var settings = SftpServerSettingsResolver.Resolve(_etlContext, ServerConfig, NodeContext());

        Assert.Equal(22, settings.Port);
        Assert.Equal(3, settings.MaxConcurrentConnections);
        Assert.Null(settings.HostKeyFingerprint);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpServerSettingsResolverTests`
Expected: build error, `SftpServerSettings` and `SftpServerSettingsResolver` do not exist.

- [x] **Step 3: Write the implementation**

`src/MeshAdapter.Sdk/Nodes/SftpServerSettings.cs`:

```csharp
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Shape of the tenant GlobalConfiguration entry that the SFTP nodes reference by name.
/// </summary>
public sealed record SftpServerSettings
{
    /// <summary>Host name or address of the SFTP server</summary>
    public required string Host { get; init; }

    /// <summary>Port, defaults to the SSH port</summary>
    public int Port { get; init; } = 22;

    /// <summary>User name to authenticate with</summary>
    public required string Username { get; init; }

    /// <summary>Password authentication; alternative to <see cref="PrivateKey" /></summary>
    public string? Password { get; init; }

    /// <summary>Private key in OpenSSH format; alternative to <see cref="Password" /></summary>
    public string? PrivateKey { get; init; }

    /// <summary>Passphrase protecting <see cref="PrivateKey" />, if any</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Upper bound of simultaneous sessions this process opens against the server</summary>
    public int MaxConcurrentConnections { get; init; } = 3;

    /// <summary>
    /// SHA-256 fingerprint of the expected host key, non-padded base64 as printed by
    /// <c>ssh-keygen -lf</c>, with or without the <c>SHA256:</c> prefix. When set, a server
    /// presenting a different key is refused. When absent, any host key is accepted, which is
    /// the behaviour of every release before this option existed.
    /// </summary>
    public string? HostKeyFingerprint { get; init; }
}
```

`src/MeshAdapter.Sdk/Nodes/SftpServerSettingsResolver.cs`:

```csharp
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Resolves the named GlobalConfiguration entry into <see cref="SftpServerSettings" /> and
/// rejects an entry that cannot authenticate. Shared by every SFTP node so the checks cannot
/// drift apart.
/// </summary>
public static class SftpServerSettingsResolver
{
    /// <summary>Resolves and validates the settings behind a server configuration name.</summary>
    public static SftpServerSettings Resolve(IMeshEtlContext etlContext, string serverConfigurationName,
        INodeContext nodeContext)
    {
        if (!etlContext.GlobalConfiguration.IsDefined(serverConfigurationName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, "ServerConfiguration", serverConfigurationName);
        }

        var settings = etlContext.GlobalConfiguration.GetValue<SftpServerSettings>(serverConfigurationName);

        if (string.IsNullOrWhiteSpace(settings.PrivateKey) && string.IsNullOrWhiteSpace(settings.Password))
        {
            throw MeshAdapterPipelineExecutionException.SftpAuthNotConfigured(nodeContext);
        }

        return settings;
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpServerSettingsResolverTests`
Expected: 3 passed. If the auth assertion fails on wording, read the message that `SftpAuthNotConfigured` actually produces and assert on a substring of it rather than changing the exception.

- [x] **Step 5: Commit**

```bash
git add src/MeshAdapter.Sdk/Nodes/Sftp tests/MeshAdapter.Sdk.Tests/Nodes/Sftp
git commit -m "AB#4846: share SFTP server settings and their validation across nodes"
```

---

### Task 2: Host key verification

The decision whether a presented key is trusted is pure logic, so it is tested here rather than against a live server.

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/SftpHostKeyVerifier.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/SftpHostKeyVerifierTests.cs`

**Interfaces:**
- Produces: `public static bool SftpHostKeyVerifier.IsTrusted(string? expectedFingerprint, string presentedFingerprintSha256)`

- [x] **Step 1: Write the failing test**

```csharp
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class SftpHostKeyVerifierTests
{
    private const string Presented = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTrusted_NoFingerprintConfigured_AcceptsAnyKey(string? expected)
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(expected, Presented));
    }

    [Fact]
    public void IsTrusted_ExactMatch_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(Presented, Presented));
    }

    [Fact]
    public void IsTrusted_SshPrefixedFingerprint_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted("SHA256:" + Presented, Presented));
    }

    [Fact]
    public void IsTrusted_PaddedFingerprint_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(Presented + "=", Presented));
    }

    [Fact]
    public void IsTrusted_SurroundingWhitespace_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted("  " + Presented + "  ", Presented));
    }

    [Fact]
    public void IsTrusted_DifferentFingerprint_Refuses()
    {
        Assert.False(SftpHostKeyVerifier.IsTrusted("2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo", Presented));
    }

    [Fact]
    public void IsTrusted_CaseDiffersInBase64Body_Refuses()
    {
        Assert.False(SftpHostKeyVerifier.IsTrusted(Presented.ToLowerInvariant(), Presented));
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpHostKeyVerifierTests`
Expected: build error, `SftpHostKeyVerifier` does not exist.

- [x] **Step 3: Write the implementation**

```csharp
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Compares a configured host key fingerprint against the one a server presented. Base64 is
/// case sensitive, so the comparison is ordinal; only the decorations people copy along with a
/// fingerprint are normalised away.
/// </summary>
public static class SftpHostKeyVerifier
{
    private const string Sha256Prefix = "SHA256:";

    /// <summary>
    /// True when the presented key may be trusted. An unset expectation trusts any key, which
    /// keeps existing configurations working unchanged.
    /// </summary>
    public static bool IsTrusted(string? expectedFingerprint, string presentedFingerprintSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return true;
        }

        return string.Equals(Normalize(expectedFingerprint), Normalize(presentedFingerprintSha256),
            StringComparison.Ordinal);
    }

    private static string Normalize(string fingerprint)
    {
        var value = fingerprint.Trim();

        if (value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Sha256Prefix.Length..];
        }

        return value.TrimEnd('=');
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpHostKeyVerifierTests`
Expected: 9 passed.

- [x] **Step 5: Commit**

```bash
git add src/MeshAdapter.Sdk/Nodes/SftpHostKeyVerifier.cs tests/MeshAdapter.Sdk.Tests/Nodes/SftpHostKeyVerifierTests.cs
git commit -m "AB#4846: verify SFTP host keys against a configured fingerprint"
```

---

### Task 3: The session seam and its SSH.NET implementation

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/SftpEntry.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/ISftpSession.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/ISftpSessionFactory.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/SshNetSftpSessionFactory.cs`
- Modify: `src/MeshAdapter.Sdk/Configuration/DependencyInjection/ServiceCollectionExtensions.cs:114`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/SshNetSftpSessionFactoryTests.cs`

**Interfaces:**
- Consumes: `SftpServerSettings`, `SftpHostKeyVerifier.IsTrusted`
- Produces:
  - `public sealed record SftpEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTime LastWriteTimeUtc)`
  - `public interface ISftpSession : IDisposable` with `IReadOnlyList<SftpEntry> List(string remoteDirectory)`, `byte[] Download(string remotePath)`, `void Upload(Stream content, string remotePath)`, `void EnsureDirectory(string remoteDirectory)`
  - `public interface ISftpSessionFactory` with `Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName, CancellationToken cancellationToken = default)`
  - `MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(string host, string expectedFingerprint, string presentedFingerprint)`

**Note on the concurrency limit.** `SftpUploadNode` keeps its semaphores in `IMeshEtlContext.Properties` today. They move into the factory, which is a singleton, so `MaxConcurrentConnections` now bounds the whole process per server configuration name instead of one ETL context. That is what the setting means against a partner server, and it is a deliberate change.

- [x] **Step 1: Write the failing test**

```csharp
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class SshNetSftpSessionFactoryTests
{
    [Fact]
    public async Task ConnectAsync_NonPositiveMaxConcurrentConnections_Throws()
    {
        var factory = new SshNetSftpSessionFactory();
        var settings = new SftpServerSettings
        {
            Host = "sftp.example.com",
            Username = "user",
            Password = "secret",
            MaxConcurrentConnections = 0
        };

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => factory.ConnectAsync(settings, "sftp-server-1"));
    }
}
```

Everything past the configuration guard needs a live server, so it is covered by the upload regression suite in Task 4 and by the staging verification, not here. Say so in the test file header rather than writing tests that fake SSH.NET itself.

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SshNetSftpSessionFactoryTests`
Expected: build error, the factory does not exist.

- [x] **Step 3: Write the implementation**

`SftpEntry.cs`:

```csharp
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>One entry of a remote directory listing.</summary>
public sealed record SftpEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTime LastWriteTimeUtc);
```

`ISftpSession.cs`:

```csharp
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// An open SFTP session. Disposing it closes the connection and releases the server's
/// concurrency slot, so callers must keep the session in a using scope.
/// </summary>
public interface ISftpSession : IDisposable
{
    /// <summary>Lists a remote directory, files and directories alike.</summary>
    IReadOnlyList<SftpEntry> List(string remoteDirectory);

    /// <summary>Reads a remote file completely into memory.</summary>
    byte[] Download(string remotePath);

    /// <summary>Writes a stream to a remote path, overwriting an existing file.</summary>
    void Upload(Stream content, string remotePath);

    /// <summary>Creates the remote directory and any missing parent, if it does not exist.</summary>
    void EnsureDirectory(string remoteDirectory);
}
```

`ISftpSessionFactory.cs`:

```csharp
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Opens SFTP sessions, honouring the per-server concurrency limit and the optional host key
/// fingerprint. Single seam for every SFTP node, so connection behaviour cannot differ between
/// the read and the write direction.
/// </summary>
public interface ISftpSessionFactory
{
    /// <summary>
    /// Waits for a free slot of <paramref name="serverConfigurationName" />, connects and
    /// returns the open session.
    /// </summary>
    Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        CancellationToken cancellationToken = default);
}
```

`SshNetSftpSessionFactory.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// SSH.NET implementation of <see cref="ISftpSessionFactory" />. One semaphore per server
/// configuration name bounds how many sessions this process opens against that server.
/// </summary>
public sealed class SshNetSftpSessionFactory : ISftpSessionFactory
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    /// <inheritdoc />
    public async Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        CancellationToken cancellationToken = default)
    {
        if (settings.MaxConcurrentConnections <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidMaxConcurrentConnections(
                serverConfigurationName, settings.MaxConcurrentConnections);
        }

        var semaphore = _semaphores.GetOrAdd(serverConfigurationName,
            _ => new SemaphoreSlim(settings.MaxConcurrentConnections, settings.MaxConcurrentConnections));

        await semaphore.WaitAsync(cancellationToken);

        SftpClient? client = null;
        try
        {
            client = CreateClient(settings);
            client.Connect();
            return new SshNetSftpSession(client, semaphore);
        }
        catch
        {
            client?.Dispose();
            semaphore.Release();
            throw;
        }
    }

    private static SftpClient CreateClient(SftpServerSettings settings)
    {
        SftpClient client;

        if (!string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey));
            var privateKeyFile = string.IsNullOrWhiteSpace(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);

            client = new SftpClient(settings.Host, settings.Port, settings.Username, [privateKeyFile]);
        }
        else
        {
            client = new SftpClient(settings.Host, settings.Port, settings.Username, settings.Password ?? string.Empty);
        }

        client.HostKeyReceived += (_, e) =>
        {
            if (SftpHostKeyVerifier.IsTrusted(settings.HostKeyFingerprint, e.FingerPrintSHA256))
            {
                return;
            }

            // Refusing here aborts Connect(); the message names both fingerprints so an
            // operator can tell a rotated key from a wrong one without reproducing the call.
            e.CanTrust = false;
            throw MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(
                settings.Host, settings.HostKeyFingerprint!, e.FingerPrintSHA256);
        };

        return client;
    }

    private sealed class SshNetSftpSession(SftpClient client, SemaphoreSlim semaphore) : ISftpSession
    {
        public IReadOnlyList<SftpEntry> List(string remoteDirectory)
        {
            return client.ListDirectory(remoteDirectory)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new SftpEntry(f.Name, f.FullName, f.IsDirectory, f.Length,
                    f.LastWriteTimeUtc))
                .ToList();
        }

        public byte[] Download(string remotePath)
        {
            using var stream = new MemoryStream();
            client.DownloadFile(remotePath, stream);
            return stream.ToArray();
        }

        public void Upload(Stream content, string remotePath)
        {
            client.UploadFile(content, remotePath, true);
        }

        public void EnsureDirectory(string remoteDirectory)
        {
            var isAbsolute = remoteDirectory.StartsWith('/');
            var parts = remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = isAbsolute ? "" : ".";

            foreach (var part in parts)
            {
                currentPath += "/" + part;
                try
                {
                    client.GetAttributes(currentPath);
                }
                catch (SftpPathNotFoundException)
                {
                    client.CreateDirectory(currentPath);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                client.Dispose();
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
```

- [x] **Step 4: Add the new exception factory**

In `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`, next to `SftpAuthNotConfigured` (line 192):

```csharp
    /// <summary>The server presented a host key other than the configured one.</summary>
    public static Exception SftpHostKeyMismatch(string host, string expectedFingerprint, string presentedFingerprint)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Host key of '{host}' does not match the configured fingerprint. Expected '{expectedFingerprint}', server presented '{presentedFingerprint}'. Update hostKeyFingerprint in the server configuration if the key was rotated deliberately.");
    }
```

Match the constructor and style the neighbouring factories use; `InvalidMaxConcurrentConnections` already exists and is reused unchanged.

- [x] **Step 5: Register the factory**

In `src/MeshAdapter.Sdk/Configuration/DependencyInjection/ServiceCollectionExtensions.cs`, beside the other singletons around line 114:

```csharp
        services.AddSingleton<ISftpSessionFactory, SshNetSftpSessionFactory>();
```

- [x] **Step 6: Run the tests**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SshNetSftpSessionFactoryTests`
Expected: 1 passed.

- [x] **Step 7: Commit**

```bash
git add src/MeshAdapter.Sdk tests/MeshAdapter.Sdk.Tests/Nodes/Sftp
git commit -m "AB#4846: add an SFTP session seam with host key pinning"
```

---

### Task 4: Move `SftpUpload@1` onto the seam

Doing this before the new nodes means the seam is proven against the existing 474-line upload suite rather than against code written for it.

**Files:**
- Modify: `src/MeshAdapter.Sdk/Nodes/Load/SftpUploadNode.cs`
- Modify: `tests/MeshAdapter.Sdk.Tests/Nodes/Load/SftpUploadNodeTests.cs`

**Interfaces:**
- Consumes: `ISftpSessionFactory.ConnectAsync`, `SftpServerSettingsResolver.Resolve`
- Produces: `public class SftpUploadNode(NodeDelegate next, IMeshEtlContext etlContext, ISftpSessionFactory sessionFactory)`

- [x] **Step 1: Point the existing tests at the new constructor**

One place changes, the `CreateNode` helper:

```csharp
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    // in the constructor:
    A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<CancellationToken>._))
        .Returns(Task.FromResult(_session));

    private SftpUploadNode CreateNode(NodeDelegate next)
    {
        return new SftpUploadNode(next, _etlContext, _sessionFactory);
    }
```

- [x] **Step 2: Write the new test the seam makes possible**

```csharp
    [Fact]
    public async Task ProcessObjectAsync_StringContent_UploadsEncodedBytesToResolvedPath()
    {
        var config = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = TestServerConfig,
            RemoteDirectory = TestRemoteDir,
            FileName = TestFileName,
            Path = TestContentPath,
            Encoding = "iso-8859-1"
        };

        A.CallTo(() => _globalConfiguration.IsDefined(TestServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(TestServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });

        var (dataContext, nodeContext, next) = PrepareTest<SftpUploadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>(TestContentPath)).Returns("Grüße");

        byte[]? uploaded = null;
        A.CallTo(() => _session.Upload(A<Stream>._, A<string>._))
            .Invokes((Stream content, string _) =>
            {
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                uploaded = buffer.ToArray();
            });

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Upload(A<Stream>._, TestRemoteDir + "/" + TestFileName)).MustHaveHappenedOnceExactly();
        Assert.NotNull(uploaded);
        // ü is a single byte in ISO-8859-1 and would be two in UTF-8.
        Assert.Equal(5, uploaded!.Length);
    }
```

- [x] **Step 3: Run the suite to verify the new test fails and the old ones do not compile**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpUploadNodeTests`
Expected: build error on the three-argument constructor.

- [x] **Step 4: Refactor the node**

In `SftpUploadNode.cs`: delete the private `SftpServerConfiguration` record, `GetOrCreateSemaphore`, `SftpSemaphoresKey`, `SemaphoresLock`, `CreateSftpClient`, `EnsureRemoteDirectoryExists` and `ValidateAuthConfiguration`. Replace the connect-and-upload block with:

```csharp
            var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);

            // Resolve file name
            var fileName = ResolveFileName(c, dataContext, nodeContext);

            // Build remote path
            var remotePath = c.RemoteDirectory.TrimEnd('/') + "/" + fileName;

            if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
            {
                // unchanged dry-run block, including RecordDryRunIntent
                await next(dataContext, nodeContext);
                return;
            }

            await using var uploadStream = await GetUploadStreamAsync(c, dataContext, nodeContext);

            using var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration);
            session.EnsureDirectory(c.RemoteDirectory);
            session.Upload(uploadStream, remotePath);
```

Keep `ValidateConfiguration`, `ResolveFileName`, `SanitizeFileName`, `GetUploadStreamAsync`, the dry-run intent and the `catch` chain ending in `CannotUploadViaSftp` exactly as they are. The settings resolution stays inside the node, so the existing tests for a missing entry and for missing authentication keep asserting against the node they always asserted against.

- [x] **Step 5: Run the full upload suite**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpUploadNodeTests`
Expected: every previously passing test still passes, plus the new one.

- [x] **Step 6: Commit**

```bash
git add src/MeshAdapter.Sdk/Nodes/Load/SftpUploadNode.cs tests/MeshAdapter.Sdk.Tests/Nodes/Load/SftpUploadNodeTests.cs
git commit -m "AB#4846: move SftpUpload onto the shared session seam"
```

---

### Task 5: The file name glob

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/Extract/SftpFileNameGlob.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpFileNameGlobTests.cs`

**Interfaces:**
- Produces: `internal static bool SftpFileNameGlob.Matches(string fileName, string pattern)`

- [x] **Step 1: Write the failing test**

```csharp
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpFileNameGlobTests
{
    [Theory]
    [InlineData("AR00006946.TXT", "AR*TXT", true)]
    [InlineData("ar00006946.txt", "AR*TXT", true)]
    [InlineData("BE_20240205035403463.txt", "BE*txt", true)]
    [InlineData("AS00006946.TXT", "AR*TXT", false)]
    [InlineData("XAR00006946.TXT", "AR*TXT", false)]
    [InlineData("AR00006946.TXT.bak", "AR*TXT", false)]
    [InlineData("AR1.TXT", "AR?.TXT", true)]
    [InlineData("AR12.TXT", "AR?.TXT", false)]
    [InlineData("report.2026.txt", "report.*.txt", true)]
    [InlineData("reportX2026.txt", "report.*.txt", false)]
    public void Matches_FollowsGlobSemantics(string fileName, string pattern, bool expected)
    {
        Assert.Equal(expected, SftpFileNameGlob.Matches(fileName, pattern));
    }
}
```

The last two pin that a dot in the pattern is a literal dot, not a regex wildcard.

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpFileNameGlobTests`
Expected: build error, the class does not exist.

- [x] **Step 3: Write the implementation**

```csharp
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// File name matching for remote listings: '*' matches any run of characters, '?' exactly one,
/// the pattern is anchored at both ends and matching is case insensitive. Every other character
/// is literal, so a dot in the pattern is a dot.
/// </summary>
internal static class SftpFileNameGlob
{
    internal static bool Matches(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }
}
```

- [x] **Step 4: Run the test to verify it passes**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpFileNameGlobTests`
Expected: 10 passed.

- [x] **Step 5: Commit**

```bash
git add src/MeshAdapter.Sdk/Nodes/Extract/SftpFileNameGlob.cs tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpFileNameGlobTests.cs
git commit -m "AB#4846: add glob matching for remote file listings"
```

---

### Task 6: `SftpList@1`

**Files:**
- Create: `src/MeshNodes.Sdk/Extract/SftpListNodeConfiguration.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/Extract/SftpListNode.cs`
- Modify: `src/MeshNodes.Sdk/Configuration/DataPipelineBuilderExtensions.cs` (extract block)
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpListNodeTests.cs`

**Interfaces:**
- Consumes: `ISftpSessionFactory`, `SftpServerSettingsResolver`, `SftpFileNameGlob.Matches`, `SftpEntry`
- Produces: `SftpListNodeConfiguration` with `ServerConfiguration`, `RemoteDirectory`, `FilePattern`, `MinFileAgeSeconds`, inherited `TargetPath`; `SftpListNode`; element shape `{ name, fullPath, length, lastWriteTimeUtc, source { serverConfiguration, remoteDirectory, filePattern } }`; `MeshAdapterPipelineExecutionException.FilePatternNotConfigured(INodeContext)`

- [x] **Step 1: Write the failing tests**

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpListNodeTests : NodeTestBase
{
    private const string ServerConfig = "LkvSftp";
    private const string RemoteDir = "/";
    private const string TargetPath = "$.files";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpListNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(_session));
    }

    private static SftpListNodeConfiguration Config(string filePattern = "AR*TXT", int minAge = 0)
    {
        return new SftpListNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemoteDirectory = RemoteDir,
            FilePattern = filePattern,
            MinFileAgeSeconds = minAge,
            TargetPath = TargetPath
        };
    }

    private static SftpEntry File(string name, DateTime? written = null, long length = 430)
    {
        return new SftpEntry(name, "/" + name, false, length, written ?? DateTime.UtcNow.AddHours(-1));
    }

    private async Task<JsonArray?> RunAsync(SftpListNodeConfiguration config)
    {
        var (dataContext, nodeContext, next) = PrepareTest<SftpListNodeConfiguration>(config);

        JsonArray? emitted = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonArray>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonArray value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                emitted = value);

        var node = new SftpListNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        return emitted;
    }

    [Fact]
    public async Task ProcessObjectAsync_FiltersByPatternAndSortsOrdinal()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>
        {
            File("AR00002.TXT"),
            File("BE00001.txt"),
            File("AR00001.TXT"),
            new("subdir", "/subdir", true, 0, DateTime.UtcNow.AddHours(-1))
        });

        var emitted = await RunAsync(Config());

        Assert.NotNull(emitted);
        Assert.Equal(2, emitted!.Count);
        Assert.Equal("AR00001.TXT", emitted[0]!["name"]!.GetValue<string>());
        Assert.Equal("AR00002.TXT", emitted[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_YoungerThanMinFileAge_IsOmitted()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>
        {
            File("AR00001.TXT", DateTime.UtcNow.AddSeconds(-5))
        });

        var emitted = await RunAsync(Config(minAge: 60));

        Assert.NotNull(emitted);
        Assert.Empty(emitted!);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoMatches_StillWritesAnEmptyArray()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry>());

        var emitted = await RunAsync(Config());

        // A downstream ForEach@1 aborts with PathMustBeArray when the path is missing.
        Assert.NotNull(emitted);
        Assert.Empty(emitted!);
    }

    [Fact]
    public async Task ProcessObjectAsync_StampsSourceOnEveryElement()
    {
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT") });

        var emitted = await RunAsync(Config());

        var source = emitted![0]!["source"]!;
        Assert.Equal(ServerConfig, source["serverConfiguration"]!.GetValue<string>());
        Assert.Equal(RemoteDir, source["remoteDirectory"]!.GetValue<string>());
        Assert.Equal("AR*TXT", source["filePattern"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_EmitsRoundTripStableTimestamp()
    {
        var written = new DateTime(2026, 8, 20, 11, 27, 35, DateTimeKind.Utc).AddTicks(8850000);
        A.CallTo(() => _session.List(RemoteDir)).Returns(new List<SftpEntry> { File("AR00001.TXT", written) });

        var first = await RunAsync(Config());
        var second = await RunAsync(Config());

        var firstStamp = first![0]!["lastWriteTimeUtc"]!.GetValue<string>();
        var secondStamp = second![0]!["lastWriteTimeUtc"]!.GetValue<string>();

        // The consumer builds a file identity from this string. If it were not stable, the
        // identity would change on every listing and nothing would ever count as processed.
        Assert.Equal(firstStamp, secondStamp);
        Assert.Equal(written, DateTime.Parse(firstStamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyFilePattern_Throws()
    {
        var config = Config(filePattern: "");
        var (dataContext, nodeContext, next) = PrepareTest<SftpListNodeConfiguration>(config);
        var node = new SftpListNode(next, _etlContext, _sessionFactory);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpListNodeTests`
Expected: build error, configuration and node do not exist.

- [x] **Step 3: Write the configuration**

```csharp
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node object for listing files on an SFTP server. Emits metadata only; the
/// content of a listed file is read with <c>SftpDownload@1</c>.
/// </summary>
[NodeName("SftpList", 1)]
public record SftpListNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Name of the global configuration for the SFTP server</summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>Remote directory to list</summary>
    [PropertyGroup("Connection", 1)]
    public required string RemoteDirectory { get; set; }

    /// <summary>
    /// Glob the file name must match: '*' any run of characters, '?' exactly one, anchored,
    /// case insensitive, every other character literal
    /// </summary>
    [PropertyGroup("Filter", 0)]
    public required string FilePattern { get; set; }

    /// <summary>
    /// Omit entries whose last write is younger than this, so a file still being written is
    /// picked up on a later run instead of being read half finished
    /// </summary>
    [PropertyGroup("Filter", 1)]
    public int MinFileAgeSeconds { get; set; }
}
```

- [x] **Step 4: Write the node**

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Lists an SFTP directory and writes one element per matching file to the target path.
/// Metadata only: the array is meant to be iterated with <c>ForEach@1</c>, reading each file
/// with <c>SftpDownload@1</c>. The array is always written, even when nothing matches, because
/// a downstream <c>ForEach@1</c> aborts on a missing iteration path.
/// </summary>
[NodeConfiguration(typeof(SftpListNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpListNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpListNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.FilePattern))
        {
            // 'required' is a C# concept; the pipeline deserializer only rejects unknown
            // properties, so a missing filePattern arrives here as an empty string.
            throw MeshAdapterPipelineExecutionException.FilePatternNotConfigured(nodeContext);
        }

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);
        var now = DateTime.UtcNow;

        List<SftpEntry> entries;
        using (var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration))
        {
            entries = session.List(c.RemoteDirectory)
                .Where(e => !e.IsDirectory)
                .Where(e => SftpFileNameGlob.Matches(e.Name, c.FilePattern))
                .Where(e => (now - e.LastWriteTimeUtc).TotalSeconds >= c.MinFileAgeSeconds)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        }

        var files = new JsonArray();
        foreach (var entry in entries)
        {
            files.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["fullPath"] = entry.FullPath,
                ["length"] = entry.Length,
                // Round-trip format on purpose: a consumer derives a file identity from this
                // string, so it has to read the same on every listing of an unchanged file.
                ["lastWriteTimeUtc"] = entry.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                ["source"] = new JsonObject
                {
                    ["serverConfiguration"] = c.ServerConfiguration,
                    ["remoteDirectory"] = c.RemoteDirectory,
                    ["filePattern"] = c.FilePattern
                }
            });
        }

        dataContext.Set(c.TargetPath, files, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        nodeContext.Debug("SftpList: {0} file(s) in '{1}' match '{2}'", files.Count, c.RemoteDirectory,
            c.FilePattern);

        await next(dataContext, nodeContext);
    }
}
```

Each element builds its own `source` object; a `JsonNode` cannot be attached to two parents, so a shared instance would throw on the second element.

- [x] **Step 5: Add the exception factory and register the node**

In `MeshAdapterPipelineExecutionException.cs`:

```csharp
    /// <summary>The list node was deployed without a file pattern.</summary>
    public static Exception FilePatternNotConfigured(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File pattern is not configured. Set 'filePattern', for example \"AR*TXT\".");
    }
```

The class is `internal` with private constructors and every factory formats its own message, so the node path goes into the string exactly as the neighbouring factories do it. In `DataPipelineBuilderExtensions.cs`, in the extract block:

```csharp
        pipelineBuilder.RegisterNodeConfiguration<SftpListNodeConfiguration>();
```

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpListNodeTests`
Expected: 6 passed. If FakeItEasy cannot bind the typed `Invokes` lambda against the generic `Set`, capture the argument through `Fake.GetCalls(dataContext)` instead of changing the node.

- [x] **Step 7: Commit**

```bash
git add src/MeshNodes.Sdk src/MeshAdapter.Sdk tests/MeshAdapter.Sdk.Tests/Nodes/Extract
git commit -m "AB#4846: add SftpList@1 to list remote files as pipeline data"
```

---

### Task 7: Content decoding

Counterpart of `SftpContentEncoder`, so the read direction handles encoding failures as deliberately as the write direction does.

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/Extract/SftpContentDecoder.cs`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpContentDecoderTests.cs`

**Interfaces:**
- Consumes: `SftpUploadEncoding.Resolve`, `EncodingErrorHandling`
- Produces: `internal static string SftpContentDecoder.Decode(byte[] content, string encodingName, EncodingErrorHandling onEncodingError, INodeContext nodeContext)`; `MeshAdapterPipelineExecutionException.CannotDecodeContent(INodeContext, string)`

- [x] **Step 1: Write the failing test**

```csharp
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpContentDecoderTests : NodeTestBase
{
    private (Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.INodeContext Context,
        Meshmakers.Octo.Sdk.Common.EtlDataPipeline.IPipelineLogger Logger) Prepare()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = "LkvSftp",
            RemotePath = "/AR00001.TXT",
            TargetPath = "$.fileContent"
        };
        var (_, nodeContext, _, logger) = PrepareTestWithLogger<SftpDownloadNodeConfiguration>(config);
        return (nodeContext, logger);
    }

    [Fact]
    public void Decode_Latin1Bytes_ReadsUmlautAsOneByte()
    {
        var (nodeContext, _) = Prepare();
        // 0xFC is 'ü' in ISO-8859-1 and an invalid UTF-8 sequence.
        var bytes = new byte[] { 0x47, 0x72, 0xFC, 0x73, 0x73, 0x65 };

        var text = SftpContentDecoder.Decode(bytes, "iso-8859-1", EncodingErrorHandling.Replace, nodeContext);

        Assert.Equal("Grüsse", text);
    }

    [Fact]
    public void Decode_InvalidUtf8WithFail_Throws()
    {
        var (nodeContext, _) = Prepare();
        var bytes = new byte[] { 0x47, 0xFC, 0x73 };

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Fail, nodeContext));
    }

    [Fact]
    public void Decode_InvalidUtf8WithReplace_ReplacesAndWarns()
    {
        var (nodeContext, logger) = Prepare();
        var bytes = new byte[] { 0x47, 0xFC, 0x73 };

        var text = SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Replace, nodeContext);

        Assert.Contains("�", text);
        A.CallTo(logger).Where(call => call.Method.Name == "Warning").MustHaveHappened();
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpContentDecoderTests`
Expected: build error, the decoder does not exist. `SftpDownloadNodeConfiguration` arrives in Task 8; if you run this task first, prepare the context with `SftpListNodeConfiguration` instead and switch it back afterwards.

- [x] **Step 3: Write the implementation**

```csharp
using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Decodes downloaded bytes honouring the configured encoding and error handling. Counterpart
/// of <see cref="Load.SftpContentEncoder" />. Single-byte code pages such as ISO-8859-1 map
/// every byte, so the failure path is reachable only for multi-byte encodings.
/// </summary>
internal static class SftpContentDecoder
{
    internal static string Decode(byte[] content, string encodingName, EncodingErrorHandling onEncodingError,
        INodeContext nodeContext)
    {
        var strict = (Encoding)SftpUploadEncoding.Resolve(encodingName).Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;

        try
        {
            return strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            if (onEncodingError == EncodingErrorHandling.Fail)
            {
                throw MeshAdapterPipelineExecutionException.CannotDecodeContent(nodeContext, encodingName);
            }

            nodeContext.Warning(
                "SftpDownload: content is not valid '{0}'; undecodable bytes were replaced. Check the encoding option against what the source system writes.",
                encodingName);

            return SftpUploadEncoding.Resolve(encodingName).GetString(content);
        }
    }
}
```

- [x] **Step 4: Add the exception factory**

```csharp
    /// <summary>Downloaded bytes are not valid in the configured encoding and Fail was chosen.</summary>
    public static Exception CannotDecodeContent(INodeContext nodeContext, string encodingName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Downloaded content is not valid '{encodingName}'. Set the correct encoding, or switch onEncodingError to Replace to accept a lossy read.");
    }
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpContentDecoderTests`
Expected: 3 passed.

- [x] **Step 6: Commit**

```bash
git add src/MeshAdapter.Sdk tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpContentDecoderTests.cs
git commit -m "AB#4846: decode downloaded content with explicit encoding handling"
```

---

### Task 8: `SftpDownload@1`

**Files:**
- Create: `src/MeshNodes.Sdk/Extract/SftpDownloadNodeConfiguration.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/Extract/SftpDownloadNode.cs`
- Modify: `src/MeshNodes.Sdk/Configuration/DataPipelineBuilderExtensions.cs` (extract block)
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpDownloadNodeTests.cs`

**Interfaces:**
- Consumes: `ISftpSessionFactory`, `SftpServerSettingsResolver`, `SftpContentDecoder.Decode`, `SftpUploadEncoding.Resolve`, `EncodingErrorHandling`
- Produces: `SftpDownloadNodeConfiguration` with `ServerConfiguration`, `RemotePath`, `RemotePathPath`, `Encoding`, `OnEncodingError`, inherited `TargetPath`; `SftpDownloadNode`; `MeshAdapterPipelineExecutionException.NoRemotePathSpecified(INodeContext)`

- [x] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpDownloadNodeTests : NodeTestBase
{
    private const string ServerConfig = "LkvSftp";
    private const string TargetPath = "$.fileContent";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly ISftpSession _session = A.Fake<ISftpSession>();

    public SftpDownloadNodeTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SftpServerSettings>(ServerConfig))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task ProcessObjectAsync_StaticPathWithLatin1_WritesDecodedContent()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            Encoding = "iso-8859-1",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/AR00001.TXT"))
            .Returns(new byte[] { 0x47, 0x72, 0xFC, 0x73, 0x73, 0x65 });

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(TargetPath, "Grüsse", A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_PathFromDataContext_TakesPrecedence()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/static.TXT",
            RemotePathPath = "$.current.fullPath",
            TargetPath = TargetPath
        };
        A.CallTo(() => _session.Download("/dynamic.TXT")).Returns("ok"u8.ToArray());

        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.current.fullPath")).Returns("/dynamic.TXT");

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _session.Download("/dynamic.TXT")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.Download("/static.TXT")).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoPathConfigured_Throws()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            TargetPath = TargetPath
        };
        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_PathResolvesToNothing_Throws()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePathPath = "$.current.fullPath",
            TargetPath = TargetPath
        };
        var (dataContext, nodeContext, next) = PrepareTest<SftpDownloadNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<string>("$.current.fullPath")).Returns(null);

        var node = new SftpDownloadNode(next, _etlContext, _sessionFactory);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public void Encoding_UnknownName_IsRejectedWhenBound()
    {
        var config = new SftpDownloadNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            RemotePath = "/AR00001.TXT",
            TargetPath = TargetPath
        };

        Assert.Throws<ArgumentException>(() => config.Encoding = "not-an-encoding");
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpDownloadNodeTests`
Expected: build error, configuration and node do not exist.

- [x] **Step 3: Write the configuration**

```csharp
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node object for downloading one file from an SFTP server. Read counterpart of
/// <c>SftpUpload@1</c>.
/// </summary>
[NodeName("SftpDownload", 1)]
public record SftpDownloadNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Name of the global configuration for the SFTP server</summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>Static remote path of the file to read (set this or <see cref="RemotePathPath" />)</summary>
    [PropertyGroup("Data Mapping", 0)]
    public string? RemotePath { get; set; }

    /// <summary>
    /// Path in the data context resolving to the remote path; takes precedence over
    /// <see cref="RemotePath" />
    /// </summary>
    [PropertyGroup("Data Mapping", 1, "jsonpath")]
    public string? RemotePathPath { get; set; }

    /// <summary>
    /// Encoding the remote file is written in (e.g. utf-8, windows-1252, iso-8859-1). Unknown
    /// names are rejected when the pipeline configuration is bound, so a typo fails the
    /// deployment instead of the first download.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public string Encoding
    {
        get => _encoding;
        set
        {
            SftpUploadEncoding.Resolve(value);
            _encoding = value;
        }
    }

    private string _encoding = "utf-8";

    /// <summary>
    /// How to handle bytes the configured encoding cannot represent: Replace substitutes the
    /// replacement character and logs a warning; Fail aborts the node, so no half-readable
    /// content travels downstream.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public EncodingErrorHandling OnEncodingError
    {
        get => _onEncodingError;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentException(
                    $"Unknown onEncodingError value '{(int)value}'. Use Replace or Fail.", nameof(value));
            }

            _onEncodingError = value;
        }
    }

    private EncodingErrorHandling _onEncodingError = EncodingErrorHandling.Replace;
}
```

- [x] **Step 4: Write the node**

```csharp
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Downloads one file from an SFTP server and writes its decoded content to the target path.
/// Read counterpart of <c>SftpUpload@1</c>, which writes exactly one file. Meant to run inside
/// a <c>ForEach@1</c> over an <c>SftpList@1</c> result, one session per file.
/// </summary>
[NodeConfiguration(typeof(SftpDownloadNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpDownloadNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpDownloadNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.RemotePath) && string.IsNullOrWhiteSpace(c.RemotePathPath))
        {
            throw MeshAdapterPipelineExecutionException.NoRemotePathSpecified(nodeContext);
        }

        var remotePath = c.RemotePath;
        if (!string.IsNullOrWhiteSpace(c.RemotePathPath))
        {
            remotePath = dataContext.Get<string>(c.RemotePathPath);
        }

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw PipelineExecutionException.ValueNotSet(nodeContext, c.RemotePathPath ?? nameof(c.RemotePath));
        }

        byte[] content;
        using (var session = await sessionFactory.ConnectAsync(
                   SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext),
                   c.ServerConfiguration))
        {
            content = session.Download(remotePath);
        }

        var text = SftpContentDecoder.Decode(content, c.Encoding, c.OnEncodingError, nodeContext);

        dataContext.Set(c.TargetPath, text, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        nodeContext.Debug("SftpDownload: read {0} byte(s) from '{1}'", content.Length, remotePath);

        await next(dataContext, nodeContext);
    }
}
```

Reading is free of side effects, so there is no dry-run branch: the downstream chain must see the content in a dry run too.

- [x] **Step 5: Add the exception factory and register the node**

```csharp
    /// <summary>Neither a static nor a resolved remote path was configured.</summary>
    public static Exception NoRemotePathSpecified(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No remote path specified. Set either 'remotePath' or 'remotePathPath'.");
    }
```

In `DataPipelineBuilderExtensions.cs`, in the extract block:

```csharp
        pipelineBuilder.RegisterNodeConfiguration<SftpDownloadNodeConfiguration>();
```

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Octo.MeshAdapter.sln --filter FullyQualifiedName~SftpDownloadNodeTests`
Expected: 5 passed. If `PipelineExecutionException.ValueNotSet` is not a `MeshAdapterPipelineExecutionException`, relax that one assertion to the base exception type rather than changing the node: `ValueNotSet` is the established way to report an unresolvable path.

- [x] **Step 7: Commit**

```bash
git add src/MeshNodes.Sdk src/MeshAdapter.Sdk tests/MeshAdapter.Sdk.Tests/Nodes/Extract/SftpDownloadNodeTests.cs
git commit -m "AB#4846: add SftpDownload@1 as the read counterpart of SftpUpload@1"
```

---

### Task 9: Documentation and full verification

**Files:**
- Modify: `CLAUDE.md` (Extract Nodes list)
- Modify: `docs/developer-guide.md` (node sections and the pipeline overview around line 862)

- [x] **Step 1: Add both nodes to `CLAUDE.md`**

In the Extract Nodes list, in the style of the entries already there:

```markdown
   - **SftpListNode** (`SftpList@1`) — Lists a remote directory over SFTP and writes one element per matching file to `TargetPath`: `name`, `fullPath`, `length`, `lastWriteTimeUtc` and a `source` object naming the `serverConfiguration`, `remoteDirectory` and `filePattern` the element came from. **Metadata only** — the content is read separately with `SftpDownload@1`, so a consumer can drop already-processed files before anything is transferred. `FilePattern` is a glob (`*` any run, `?` one character, anchored, case insensitive, every other character literal); `MinFileAgeSeconds` omits entries still being written. Directory entries are excluded, the result is ordered by name (ordinal), and **an empty result still writes an empty array** because a downstream `ForEach@1` aborts with `PathMustBeArray` on a missing iteration path. `lastWriteTimeUtc` is emitted with the round-trip specifier `"O"`: consumers derive a file identity from that string, so it must read identically on every listing of an unchanged file. Connection, concurrency limit and host key pinning come from the shared `ISftpSessionFactory`.
   - **SftpDownloadNode** (`SftpDownload@1`) — Downloads exactly one file and writes its decoded content to `TargetPath`. Read counterpart of `SftpUpload@1`, which writes exactly one file, and designed to run inside a `ForEach@1` over an `SftpList@1` result. The remote path is static (`RemotePath`) or resolved from the data context (`RemotePathPath`, takes precedence). `Encoding` defaults to `utf-8` and is validated when the configuration is bound, so a typo fails the deployment rather than the first download; `OnEncodingError` chooses between a lossy read with a warning and failing the node. No dry-run branch: reading has no side effects and the downstream chain must see the content in a dry run too.
```

- [x] **Step 2: Add both nodes to `docs/developer-guide.md`**

Add an `#### SftpListNode` and an `#### SftpDownloadNode` section with the same parameter-table layout the `#### SftpUploadNode` section at line 686 uses, and extend the pipeline overview so the extract line names them:

```
  ├─ Extract Nodes (GetRtEntities*, GetAssociationTargets, SftpList, SftpDownload, etc.)
```

Also document `hostKeyFingerprint` in the `SftpUploadNode` section: optional, SHA-256 non-padded base64 as printed by `ssh-keygen -lf`, absent means any host key is accepted as before.

- [x] **Step 3: Verify the generated pipeline schema**

The adapter generates `pipeline-schema.json` after every build (`src/MeshAdapter/MeshAdapter.csproj:46-48`, target `GeneratePipelineSchema`), and that file is what a pipeline author validates against. It is the objective proof that both nodes are registered with the intended surface, independent of any test.

```bash
dotnet build Octo.MeshAdapter.sln
python -c "
import json, io
s = json.load(io.open('bin/Debug/net10.0/pipeline-schema.json', encoding='utf-8'))
for v in s['\$defs']['TransformationNode']['oneOf']:
    t = v.get('properties', {}).get('type', {}).get('const')
    if t in ('SftpList@1', 'SftpDownload@1'):
        print(t, 'required:', v.get('required'))
        print('  props:', sorted(v.get('properties', {}).keys()))
"
```

Expected, derived from how the existing nodes appear in the current schema:

- `SftpList@1` required `['serverConfiguration', 'remoteDirectory', 'filePattern', 'type']`, properties additionally `minFileAgeSeconds`, `description`, and the four a `TargetPathNodeConfiguration` contributes: `targetPath`, `documentMode`, `targetValueKind`, `targetValueWriteMode`.
- `SftpDownload@1` required `['serverConfiguration', 'type']`, properties additionally `remotePath`, `remotePathPath`, `encoding`, `onEncodingError`, `description`, plus the same four.

Both extract nodes appear under `TransformationNode`, not under a separate extract section; `SftpUpload@1` sits there too. A node missing from the schema was never registered, whatever the tests say.

- [x] **Step 4: Run the whole suite in both configurations**

```bash
dotnet format --verify-no-changes
dotnet test Octo.MeshAdapter.sln
dotnet test Octo.MeshAdapter.sln -c Release
```

Expected: no formatting differences, every test green in both configurations, zero warnings. Do not use `-c DebugL`; it resolves Octo packages from the stale local feed.

- [x] **Step 5: Commit**

```bash
git add CLAUDE.md docs/developer-guide.md
git commit -m "AB#4846: document the SFTP list and download nodes"
```

- [ ] **Step 6: Review gate before the PR**

An independent review pass over the whole diff, by a different model or session than the one that wrote it, per the project's pre-PR rule. Only then open the PR, with a body in English, plain hyphens only, and no test-status boilerplate.

---

## Self-Review

**Spec coverage.** `SftpList@1` is Task 6, `SftpDownload@1` Task 8, the shared connection layer Tasks 1 and 3, host key pinning Tasks 2 and 3, the encoding option Tasks 7 and 8, the round-trip-stable timestamp Task 6 with its own test, the `SftpUpload@1` refactor Task 4, documentation Task 9. The spec's error-handling matrix maps onto Tasks 1, 3, 6, 7 and 8. The gate node, the confirm node, the AR/BE YAML and the staging verification are adapter-side and belong to the second plan, as stated in the scope note.

**Types.** `SftpServerSettings`, `SftpEntry`, `ISftpSession`, `ISftpSessionFactory.ConnectAsync`, `SftpServerSettingsResolver.Resolve`, `SftpHostKeyVerifier.IsTrusted`, `SftpFileNameGlob.Matches` and `SftpContentDecoder.Decode` are used in later tasks exactly as defined in the earlier ones.

**Known ordering constraint.** Task 7's test file prepares its node context with `SftpDownloadNodeConfiguration`, which Task 8 creates. Run the tasks in order, or use `SftpListNodeConfiguration` there and switch it back, as the task notes.
