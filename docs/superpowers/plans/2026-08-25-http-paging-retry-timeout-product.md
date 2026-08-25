# HTTP paging, retry and timeout for MakeHttpRequest@1 (product PR) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the standard node `MakeHttpRequest@1` with paged fetching, retry with backoff, a per-attempt timeout, access from a configured GlobalConfiguration entry and an opt-in loud failure mode, so the WeClapp source pipelines can be composed from standard nodes.

**Architecture:** Four additive, independently inert capabilities. Settings resolution follows the repository's configured-node pattern (`IMeshEtlContext.GlobalConfiguration.GetValue<T>`). One request including its retries lives in a small sender unit; the node orchestrates the page walk, decides what a failure means (`OnHttpError`) and writes the result once.

**Tech Stack:** .NET 10, xUnit, FakeItEasy, System.Text.Json, `TimeProvider` for testable delays.

**Spec:** `docs/superpowers/specs/2026-08-25-http-paging-retry-timeout-design.md` (this repository; the code lives in `octo-mesh-adapter`)

## Global Constraints

- **Code repository:** `C:\Users\martin-lt\Development\meshmakers\octo-mesh-adapter`, main checkout, branch `feature/ab4846-http-fetch-node` off `main` (`1865177`). This plan and the spec stay in `octo-adapter-weclapp`.
- **Build/test:** `dotnet build -c Debug` and `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug`. Before the PR the same suite in `-c Release`. **Never `-c DebugL`** - it restores from the stale local `../nuget` feed.
- **No behaviour change without opt-in:** a consumer that sets none of the new properties must see byte-identical behaviour, including the existing log-and-stop failure path and the existing `ValidateConfiguration` reporting.
- **Every new optional integer** resolves to its documented default when the property is omitted and when the pipeline definition carries an explicit null, without throwing (`JsonNullAsDefaultAttribute` exists for the settings-record path).
- **Never log or embed the API key** in a message, exception or `ToString`.
- **Language:** code, comments, documentation, commit messages in English. No internal references, ticket shorthand or review labels in code comments.
- **Commits:** short English subjects starting `AB#4846: `, plain hyphens only, **no trailer** - attribution is settled by the squash.
- **Two verified facts this plan relies on** (checked in source while it was written, so no task needs to re-derive them):
  - The fetch core being replaced waits **after** a failed attempt: `base * 2^(attempt-1)`, i.e. 1 s, 2 s, 4 s for a base of 1 across four attempts (`WeClappFetchTriggerNode.cs:495-499`). Parity with that is the criterion.
  - `ForEachNode` is public in `Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control` and lives in the same SDK assembly as `INodeContext`, so the test project can reference it. Its isolation filter is `catch (Exception e) when (iterationErrors is not null && e is not OperationCanceledException)`.
- **Push and PR only after Martin's explicit approval**, and only after the Fable/max review gate over the full diff.

## File Structure

| File | Responsibility |
|---|---|
| `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs` (modify) | New properties, the nested `HttpPagingOptions` / `HttpRetryOptions` records, the `HttpErrorHandling` enum |
| `src/MeshAdapter.Sdk/Nodes/HttpApiSettings.cs` (create) | Two-string settings record with a masking `ToString` |
| `src/MeshAdapter.Sdk/Nodes/HttpApiSettingsResolver.cs` (create) | Named entry to settings, with the typed errors |
| `src/MeshAdapter.Sdk/Nodes/Transform/HttpRequestSender.cs` (create) | One request: attempts, per-attempt timeout, transient classification, backoff, body truncation |
| `src/MeshAdapter.Sdk/Nodes/Transform/MakeHttpRequestNode.cs` (modify) | Orchestration: resolve, page walk, `OnHttpError`, write `TargetPath` |
| `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs` (modify) | New factory methods |
| `tests/MeshAdapter.Sdk.Tests/Helpers/SequencedHttpMessageHandler.cs` (create) | Handler that answers a queued script of responses and exceptions, recording the requests |
| `tests/MeshAdapter.Sdk.Tests/Nodes/HttpApiSettingsResolverTests.cs` (create) | Resolver behaviour |
| `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/HttpRequestSenderTests.cs` (create) | Retry, timeout, classification, truncation |
| `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestNodeTests.cs` (modify) | Node behaviour: unchanged paths, configured access, paging, `OnHttpError`, ForEach isolation |
| `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestConfigurationDeserializationTests.cs` (create) | What a real pipeline definition, including explicit nulls, produces |
| `docs/developer-guide.md`, `CLAUDE.md`, `docs/test-concept.md` (modify) | Documentation |

---

### Task 1: Configured access - settings and their resolution

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/HttpApiSettings.cs`
- Create: `src/MeshAdapter.Sdk/Nodes/HttpApiSettingsResolver.cs`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/HttpApiSettingsResolverTests.cs`

**Interfaces:**
- Consumes: `IMeshEtlContext.GlobalConfiguration` (`IsDefined`, `GetValue<T>`), `INodeContext` for error text.
- Produces: `HttpApiSettings { string BaseUrl; string ApiKey; }` and `HttpApiSettingsResolver.Resolve(IMeshEtlContext etlContext, string apiConfigurationName, INodeContext nodeContext) -> HttpApiSettings`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MeshAdapter.Sdk.Tests/Nodes/HttpApiSettingsResolverTests.cs
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class HttpApiSettingsResolverTests : NodeTestBase
{
    private const string Entry = "WeClappApi";
    private const string Key = "super-secret-token-value";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    public HttpApiSettingsResolverTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private INodeContext NodeContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.result", ApiConfiguration = Entry
        };
        var (_, nodeContext, _) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        return nodeContext;
    }

    [Fact]
    public void Resolve_EntryDefined_ReturnsSettings()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry))
            .Returns(new HttpApiSettings { BaseUrl = "https://host/api/v1", ApiKey = Key });

        var settings = HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext());

        Assert.Equal("https://host/api/v1", settings.BaseUrl);
        Assert.Equal(Key, settings.ApiKey);
    }

    [Fact]
    public void Resolve_EntryNotDefined_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(false);

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
        Assert.Contains(Entry, ex.Message);
    }

    [Theory]
    [InlineData("", Key)]
    [InlineData("   ", Key)]
    [InlineData("https://host/api/v1", "")]
    [InlineData("https://host/api/v1", "   ")]
    public void Resolve_IncompleteEntry_Throws(string baseUrl, string apiKey)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry))
            .Returns(new HttpApiSettings { BaseUrl = baseUrl, ApiKey = apiKey });

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
        Assert.Contains(Entry, ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public void Resolve_NullPayload_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry)).Returns(null!);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
    }

    [Fact]
    public void ToString_MasksTheKey()
    {
        var text = new HttpApiSettings { BaseUrl = "https://host/api/v1", ApiKey = Key }.ToString();

        Assert.DoesNotContain(Key, text);
        Assert.Contains("https://host/api/v1", text);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~HttpApiSettingsResolverTests`
Expected: compile error - `HttpApiSettings`, `HttpApiSettingsResolver` and `ApiConfiguration` do not exist yet.

- [ ] **Step 3: Add the settings record**

```csharp
// src/MeshAdapter.Sdk/Nodes/HttpApiSettings.cs
namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// HTTP API access resolved from a tenant GlobalConfiguration entry. The members are deliberately
/// not <c>required</c>: a half-filled entry has to reach the resolver's message instead of failing
/// deserialization with a JSON path.
/// </summary>
public record HttpApiSettings
{
    /// <summary>API base, for example "https://tenant.example.com/webapp/api/v1".</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The key sent in the configured auth header - never log it.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Records synthesize a ToString over every member; keep the key out of it.</summary>
    public override string ToString() => $"HttpApiSettings {{ BaseUrl = {BaseUrl}, ApiKey = *** }}";
}
```

- [ ] **Step 4: Add the exception factories**

Append to `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`, next to the existing SFTP factories:

```csharp
    public static Exception InvalidHttpApiConfiguration(INodeContext nodeContext, string configurationName,
        Exception inner)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration '{configurationName}' cannot be read as HTTP API settings.",
            inner);
    }

    public static Exception IncompleteHttpApiConfiguration(INodeContext nodeContext, string configurationName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration '{configurationName}' must provide both 'baseUrl' and 'apiKey'.");
    }
```

If the exception type has no constructor taking an inner exception, add one alongside the existing constructors, following the class's current style.

- [ ] **Step 5: Add the resolver**

```csharp
// src/MeshAdapter.Sdk/Nodes/HttpApiSettingsResolver.cs
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Resolves the named GlobalConfiguration entry into <see cref="HttpApiSettings" />. A configured
/// entry that is missing or half-filled fails loudly: it is a configuration mistake, and answering
/// it with a log would leave an operator with a green execution that called nothing.
/// </summary>
internal static class HttpApiSettingsResolver
{
    public static HttpApiSettings Resolve(IMeshEtlContext etlContext, string apiConfigurationName,
        INodeContext nodeContext)
    {
        if (!etlContext.GlobalConfiguration.IsDefined(apiConfigurationName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, "ApiConfiguration", apiConfigurationName);
        }

        HttpApiSettings? settings;
        try
        {
            settings = etlContext.GlobalConfiguration.GetValue<HttpApiSettings>(apiConfigurationName);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpApiConfiguration(
                nodeContext, apiConfigurationName, e);
        }

        // A ConfigurationValue of literal null deserializes to null despite the non-null contract.
        if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw MeshAdapterPipelineExecutionException.IncompleteHttpApiConfiguration(
                nodeContext, apiConfigurationName);
        }

        return settings;
    }
}
```

- [ ] **Step 6: Add `ApiConfiguration` to the node configuration so the tests compile**

In `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`, inside `MakeHttpRequestNodeConfiguration`:

```csharp
        /// <summary>
        /// Name of a GlobalConfiguration entry providing the API base URL and key. When set, the
        /// request URL is a path relative to that base and the key is sent in
        /// <see cref="AuthHeaderName" />.
        /// </summary>
        [PropertyGroup("Connection", 5)]
        public string? ApiConfiguration { get; set; }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~HttpApiSettingsResolverTests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/MeshAdapter.Sdk/Nodes/HttpApiSettings.cs src/MeshAdapter.Sdk/Nodes/HttpApiSettingsResolver.cs src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs tests/MeshAdapter.Sdk.Tests/Nodes/HttpApiSettingsResolverTests.cs
git commit -m "AB#4846: resolve HTTP API access from a configured entry"
```

---

### Task 2: The node uses the configured access

**Files:**
- Modify: `src/MeshAdapter.Sdk/Nodes/Transform/MakeHttpRequestNode.cs`
- Modify: `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestNodeTests.cs`

**Interfaces:**
- Consumes: `HttpApiSettingsResolver.Resolve` from Task 1.
- Produces: node constructor `MakeHttpRequestNode(NodeDelegate next, HttpClient httpClient, IMeshEtlContext etlContext, TimeProvider? timeProvider = null)`; configuration properties `AuthHeaderName` (default `"Authorization"`) and `AuthHeaderValuePrefix` (default `""`); internal `static string CombineUrl(string baseUrl, string relativeUrl)`.

- [ ] **Step 1: Write the failing tests**

Add to `MakeHttpRequestNodeTests`. The existing tests construct the node with two arguments; update the existing call sites to the new constructor in the same step (a faked `IMeshEtlContext` with no configured entry is enough for them).

```csharp
    private static readonly IMeshEtlContext EmptyEtlContext = CreateEtlContext(null);

    private static IMeshEtlContext CreateEtlContext(HttpApiSettings? settings, string entry = "TestApi")
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined(entry)).Returns(settings is not null);
        if (settings is not null)
        {
            A.CallTo(() => globalConfiguration.GetValue<HttpApiSettings>(entry)).Returns(settings);
        }

        return etlContext;
    }

    [Theory]
    [InlineData("https://host/api/v1", "/article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1/", "/article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1", "article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1/", "article", "https://host/api/v1/article")]
    public async Task ProcessObjectAsync_WithApiConfiguration_JoinsBaseAndPath(
        string baseUrl, string path, string expected)
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = path, TargetPath = "$.response",
            ApiConfiguration = "TestApi", AuthHeaderName = "AuthenticationToken"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"result\":[]}"));
        var etlContext = CreateEtlContext(new HttpApiSettings { BaseUrl = baseUrl, ApiKey = "token-1" });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(expected, handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData("", "token-1")]
    [InlineData("Bearer ", "Bearer token-1")]
    public async Task ProcessObjectAsync_WithApiConfiguration_SendsAuthHeader(string prefix, string expected)
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.response",
            ApiConfiguration = "TestApi", AuthHeaderName = "AuthenticationToken",
            AuthHeaderValuePrefix = prefix
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"result\":[]}"));
        var etlContext = CreateEtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = "token-1"
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(expected, handler.Requests.Single().Headers.GetValues("AuthenticationToken").Single());
    }

    [Fact]
    public async Task ProcessObjectAsync_AbsoluteUrlWithApiConfiguration_ThrowsAndSendsNothing()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://elsewhere.example.com/article", TargetPath = "$.response",
            ApiConfiguration = "TestApi"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var etlContext = CreateEtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = "token-1"
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProcessObjectAsync_UndefinedApiConfiguration_ThrowsWithDefaultErrorHandling()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.response", ApiConfiguration = "TestApi"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), CreateEtlContext(null));

        // OnHttpError is untouched here: a configuration mistake is never a runtime outcome.
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("TestApi", ex.Message);
        Assert.Empty(handler.Requests);
    }
```

- [ ] **Step 2: Add the test helper the tests need**

```csharp
// tests/MeshAdapter.Sdk.Tests/Helpers/SequencedHttpMessageHandler.cs
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Answers a scripted sequence of outcomes and records every request it saw. A step is either a
/// response or an exception to throw, so retry and paging behaviour can be pinned without a
/// server. The last step repeats once the script runs out, which keeps a paging test from having
/// to script the exact number of pages it expects.
/// </summary>
public sealed class SequencedHttpMessageHandler(
    params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    : HttpMessageHandler
{
    private int _callCount;

    public List<HttpRequestMessage> Requests { get; } = [];

    public int CallCount => _callCount;

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Json(string body,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(
        HttpStatusCode statusCode, string body = "")
    {
        return (_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throws(
        Exception exception)
    {
        return (_, _) => Task.FromException<HttpResponseMessage>(exception);
    }

    /// <summary>A target that accepts the request and never answers, so only a timeout ends it.</summary>
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hangs()
    {
        return async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new UnreachableException();
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var index = Math.Min(_callCount, steps.Length - 1);
        _callCount++;
        return steps[index](request, cancellationToken);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestNodeTests`
Expected: compile error - the node has no three-argument constructor and the configuration has no `AuthHeaderName`.

- [ ] **Step 4: Add the configuration properties**

```csharp
        /// <summary>
        /// Header the key from <see cref="ApiConfiguration" /> is sent in. The key is inserted as
        /// it is; with the default header and no prefix it goes out scheme-less, which suits a
        /// target expecting a bare token.
        /// </summary>
        [PropertyGroup("Connection", 6)]
        public string AuthHeaderName { get; set; } = "Authorization";

        /// <summary>
        /// Scheme prefix placed before the key, for example "Bearer ". Empty by default.
        /// </summary>
        [PropertyGroup("Connection", 7)]
        public string AuthHeaderValuePrefix { get; set; } = "";
```

- [ ] **Step 5: Add the URL rejection factory**

```csharp
    public static Exception AbsoluteUrlWithHttpApiConfiguration(INodeContext nodeContext, string url)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: URL '{url}' is absolute while an ApiConfiguration is set. " +
            "Configure a path relative to the configured base URL, or drop the ApiConfiguration and " +
            "supply the header yourself - the configured key must not be sent to another host.");
    }
```

- [ ] **Step 6: Wire it into the node**

Change the declaration and resolve the settings before the existing work. Resolution happens outside any catch that reports rather than throws:

```csharp
public class MakeHttpRequestNode(
    NodeDelegate next,
    HttpClient httpClient,
    IMeshEtlContext etlContext,
    TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

In `ProcessObjectAsync`, after `ValidateConfiguration` and after `GetUrl`, before the request is built:

```csharp
        HttpApiSettings? apiSettings = null;
        if (!string.IsNullOrWhiteSpace(c.ApiConfiguration))
        {
            // Outside the try below on purpose: a configuration mistake is not a runtime outcome
            // and must not be answered with a log line.
            apiSettings = HttpApiSettingsResolver.Resolve(etlContext, c.ApiConfiguration, nodeContext);

            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw MeshAdapterPipelineExecutionException.AbsoluteUrlWithHttpApiConfiguration(nodeContext, url);
            }

            url = CombineUrl(apiSettings.BaseUrl, url);
        }
```

and, where the headers are attached:

```csharp
            if (apiSettings is not null)
            {
                request.Headers.Add(c.AuthHeaderName, c.AuthHeaderValuePrefix + apiSettings.ApiKey);
            }
```

with the helper:

```csharp
    internal static string CombineUrl(string baseUrl, string relativeUrl)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestNodeTests`
Expected: PASS, including every pre-existing test in the class.

- [ ] **Step 8: Commit**

```bash
git add src/MeshAdapter.Sdk src/MeshNodes.Sdk tests/MeshAdapter.Sdk.Tests
git commit -m "AB#4846: address a configured API from MakeHttpRequest"
```

---

### Task 3: One request with retries and a per-attempt timeout

**Files:**
- Create: `src/MeshAdapter.Sdk/Nodes/Transform/HttpRequestSender.cs`
- Modify: `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/HttpRequestSenderTests.cs`

**Interfaces:**
- Consumes: `SequencedHttpMessageHandler` from Task 2.
- Produces: `HttpRetryOptions { int MaxAttempts = 1; double BackoffBaseSeconds = 1; }`, configuration properties `Retry` and `TimeoutSeconds`, and
  `internal static Task<HttpResponseMessage> HttpRequestSender.SendAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, HttpRetryOptions retry, int? timeoutSeconds, TimeProvider timeProvider, INodeContext nodeContext)`
  which either returns a successful response or throws `MeshAdapterPipelineExecutionException`.

A fresh `HttpRequestMessage` per attempt is why the sender takes a factory: a message that has been sent cannot be sent again.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/HttpRequestSenderTests.cs
using System.Net;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class HttpRequestSenderTests : NodeTestBase
{
    private static readonly HttpRetryOptions FourAttempts = new() { MaxAttempts = 4, BackoffBaseSeconds = 1 };

    private INodeContext NodeContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response"
        };
        var (_, nodeContext, _) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        return nodeContext;
    }

    private static Func<HttpRequestMessage> Get(string url = "https://host/api")
    {
        return () => new HttpRequestMessage(HttpMethod.Get, url);
    }

    /// <summary>
    /// Drives virtual time forward in small steps until the pending call finishes, so a test never
    /// waits on the wall clock. The step is fine enough that the gaps between attempts stay exact
    /// for the backoff assertions.
    /// </summary>
    private static async Task<T> WithAdvancingTime<T>(FakeTimeProvider time, Task<T> pending)
    {
        while (!pending.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        return await pending;
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SendAsync_TransientStatusThenSuccess_Succeeds(HttpStatusCode transient)
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(transient, "busy"),
            SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task SendAsync_TransientExceptionThenSuccess_Succeeds(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(exception),
            SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SendAsync_NonTransientStatus_FailsOnFirstAttempt(HttpStatusCode status)
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Status(status, "nope"));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task SendAsync_AttemptsExhausted_ReportsStatusAttemptsAndTruncatedBody()
    {
        var body = new string('x', 500);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, body));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(4, handler.CallCount);
        Assert.Contains("503", ex.Message);
        Assert.Contains("4 attempts", ex.Message);
        Assert.Contains(new string('x', 300), ex.Message);
        Assert.DoesNotContain(new string('x', 301), ex.Message);
    }

    [Fact]
    public async Task SendAsync_ExhaustedTimeouts_ThrowsTypedNotCancellation()
    {
        // ForEach@1 isolates every exception except OperationCanceledException, and
        // TaskCanceledException derives from it: a timeout that escaped raw would abort a whole
        // loop instead of failing one iteration.
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(new TaskCanceledException()));
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, 5, time, NodeContext())));

        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task SendAsync_Backoff_WaitsAfterEachFailedAttempt()
    {
        // Parity with the fetch core being replaced: the wait happens AFTER a failed attempt and
        // doubles, so four attempts with a base of one second wait 1 s, 2 s, 4 s. Measured as the
        // virtual time between attempts - FakeTimeProvider exposes no list of pending timers, and
        // the gap between two attempts is what the pipeline actually experiences.
        var time = new FakeTimeProvider();
        var attemptTimes = new List<DateTimeOffset>();
        var handler = new SequencedHttpMessageHandler((_, _) =>
        {
            attemptTimes.Add(time.GetUtcNow());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("busy")
            });
        });

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), FourAttempts, null, time, NodeContext())));

        Assert.Equal(4, attemptTimes.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), attemptTimes[1] - attemptTimes[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), attemptTimes[2] - attemptTimes[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), attemptTimes[3] - attemptTimes[2]);
    }

    [Fact]
    public async Task SendAsync_TargetNeverAnswers_TimesOutPerAttempt()
    {
        // The timeout itself, not a thrown TaskCanceledException standing in for it: the handler
        // accepts the request and never answers, so only the node's own cancellation source ends it.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Hangs());
        var time = new FakeTimeProvider();

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 },
                10, time, NodeContext())));

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task SendAsync_DefaultOptions_MakesExactlyOneAttempt()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));
        var time = new FakeTimeProvider();

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => WithAdvancingTime(time, HttpRequestSender.SendAsync(
                new HttpClient(handler), Get(), new HttpRetryOptions(), null, time, NodeContext())));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_ZeroOrNegativeAttempts_StillTriesOnce()
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        var time = new FakeTimeProvider();

        using var response = await WithAdvancingTime(time, HttpRequestSender.SendAsync(
            new HttpClient(handler), Get(), new HttpRetryOptions { MaxAttempts = 0 }, null, time,
            NodeContext()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }
}
```

If `Microsoft.Extensions.TimeProvider.Testing` is not yet referenced by the test project, add the package reference in this step, matching the version style of the other test dependencies in `tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj`. If the repository prefers not to add a dependency, replace `FakeTimeProvider` with a minimal in-repo fake that completes `Delay` immediately and records the requested delays, and assert on those recorded values instead.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~HttpRequestSenderTests`
Expected: compile error - `HttpRequestSender` and `HttpRetryOptions` do not exist.

- [ ] **Step 3: Add the retry options to the configuration**

In `MakeHttpRequestNodeConfiguration.cs`, above the configuration record:

```csharp
    /// <summary>
    /// Retry behaviour for one request. Absent means a single attempt, which is what the node did
    /// before the option existed.
    /// </summary>
    public record HttpRetryOptions
    {
        /// <summary>Total attempts per request, so 1 means no retry.</summary>
        public int MaxAttempts { get; set; } = 1;

        /// <summary>Delay before attempt n is base * 2^(n-1) seconds; 0 disables waiting.</summary>
        public double BackoffBaseSeconds { get; set; } = 1;
    }
```

and inside the configuration record:

```csharp
        /// <summary>
        /// Retry behaviour for transient failures: 5xx, 408, 429, network errors and timeouts.
        /// Absent means a single attempt.
        /// </summary>
        /// <remarks>
        /// Nullable on purpose. A definition carrying an explicit null overwrites a property
        /// initializer, so a non-nullable property with a default would hand the node a null it
        /// cannot see coming - the same shape of mistake that a null integer in a settings entry
        /// once caused. Read it through <c>Retry ?? new HttpRetryOptions()</c> at every use site.
        /// </remarks>
        [PropertyGroup("Connection", 8)]
        public HttpRetryOptions? Retry { get; set; }

        /// <summary>
        /// Timeout in seconds applied to each attempt. Unset leaves the HTTP client's own default
        /// in place; the client is shared, so its timeout is never changed.
        /// </summary>
        [PropertyGroup("Connection", 9)]
        public int? TimeoutSeconds { get; set; }
```

- [ ] **Step 4: Add the failure factory**

```csharp
    public static Exception HttpRequestFailed(INodeContext nodeContext, string url, int? statusCode,
        int attempts, string detail)
    {
        var status = statusCode is null ? "no response" : $"HTTP {statusCode}";
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Request to '{url}' failed after {attempts} attempts ({status}): {detail}");
    }
```

- [ ] **Step 5: Implement the sender**

```csharp
// src/MeshAdapter.Sdk/Nodes/Transform/HttpRequestSender.cs
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Sends one request, retrying transient failures. Everything it can fail with leaves as a
/// <see cref="MeshAdapterPipelineExecutionException" />: a raw cancellation would escape the
/// per-iteration isolation of a surrounding loop, which treats cancellation as a reason to stop
/// altogether.
/// </summary>
internal static class HttpRequestSender
{
    private const int MaxDetailLength = 300;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        Func<HttpRequestMessage> requestFactory, HttpRetryOptions retry, int? timeoutSeconds,
        TimeProvider timeProvider, INodeContext nodeContext)
    {
        var attempts = Math.Max(1, retry.MaxAttempts); // a misconfigured 0 must still try once
        string url = "";
        int? lastStatus = null;
        var lastDetail = "no detail";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // The TimeProvider overload, so the timeout is measured on the same clock as the
            // backoff and a test can drive it instead of waiting for the wall clock.
            using var timeoutSource = timeoutSeconds is > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value), timeProvider)
                : null;

            try
            {
                var request = requestFactory();
                url = request.RequestUri?.ToString() ?? url;
                var response = await client.SendAsync(request,
                    timeoutSource?.Token ?? CancellationToken.None);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                var body = Truncate(await response.Content.ReadAsStringAsync(), MaxDetailLength);
                response.Dispose();

                if (!IsTransient(status))
                {
                    throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
                        nodeContext, url, status, attempt, body);
                }

                lastStatus = status;
                lastDetail = body;
            }
            catch (HttpRequestException e)
            {
                lastStatus = null;
                lastDetail = e.Message;
            }
            catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
            {
                // The only cancellation reaching here is this node's own timeout: the pipeline
                // hands nodes no token to observe.
                lastStatus = null;
                lastDetail = timeoutSeconds is > 0
                    ? $"the attempt exceeded the configured timeout of {timeoutSeconds} s"
                    : "the request was cancelled";
            }

            if (attempt < attempts && retry.BackoffBaseSeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(retry.BackoffBaseSeconds * Math.Pow(2, attempt - 1)),
                    timeProvider);
            }
        }

        throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
            nodeContext, url, lastStatus, attempts, lastDetail);
    }

    private static bool IsTransient(int statusCode)
    {
        return statusCode >= 500 || statusCode == 408 || statusCode == 429;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Never split a UTF-16 surrogate pair at the cut.
        if (char.IsHighSurrogate(value[maxLength - 1]))
        {
            maxLength--;
        }

        return value[..maxLength];
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~HttpRequestSenderTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MeshAdapter.Sdk src/MeshNodes.Sdk tests/MeshAdapter.Sdk.Tests
git commit -m "AB#4846: retry transient HTTP failures with a per-attempt timeout"
```

---

### Task 4: Failure semantics - `onHttpError`

**Files:**
- Modify: `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`
- Modify: `src/MeshAdapter.Sdk/Nodes/Transform/MakeHttpRequestNode.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestNodeTests.cs`

**Interfaces:**
- Consumes: `HttpRequestSender.SendAsync` from Task 3.
- Produces: `enum HttpErrorHandling { LogAndStop, Throw }` and the configuration property `OnHttpError` defaulting to `LogAndStop`. From here on the node routes every request through the sender.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task ProcessObjectAsync_FailingStatusWithDefaults_LogsStopsAndDoesNotThrow()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Error(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_RetriesExhaustedWithDefaults_StaysQuiet()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response",
            Retry = new HttpRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 },
            TimeoutSeconds = 5
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_FailingStatusWithThrow_Throws()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response",
            OnHttpError = HttpErrorHandling.Throw
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task ProcessObjectAsync_ThrowMode_IsIsolatedByForEachContinueOnError()
    {
        // The isolation the per-item composition depends on, exercised through the real loop node
        // rather than asserted about the exception type: ForEach@1 absorbs every exception except
        // OperationCanceledException, so the node's failure has to be one it can absorb.
        var services = new ServiceCollection();
        var builder = services.AddDataPipeline();
        builder.RegisterNode(typeof(ForEachNode));
        builder.RegisterNode(typeof(MakeHttpRequestNode));

        // Second item fails permanently, the others answer.
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"ok\":1}"),
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"),
            SequencedHttpMessageHandler.Json("{\"ok\":3}"));
        services.AddSingleton(new HttpClient(handler));
        services.AddSingleton(EmptyEtlContext);

        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(
            JsonDocument.Parse("{\"orders\":[{\"id\":1},{\"id\":2},{\"id\":3}]}"));
        var forEachConfig = new ForEachNodeConfiguration
        {
            IterationPath = "$.orders",
            KeyPath = "$.key",
            TargetPath = "$.loopResult",
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true,
            Transformations =
            [
                new MakeHttpRequestNodeConfiguration
                {
                    Method = "GET", Url = "https://host/api/customer", TargetPath = "$.customer",
                    OnHttpError = HttpErrorHandling.Throw
                }
            ]
        };

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(), logger, dataContext, null, null, null);
        var nodeContext = rootNodeContext.RegisterChildNode("ForEach", 0, forEachConfig, dataContext);
        var next = A.Fake<NodeDelegate>();

        var ex = await Assert.ThrowsAsync<PipelineExecutionException>(
            () => new ForEachNode(next).ProcessObjectAsync(dataContext, nodeContext));

        // One aggregated failure naming the failed index, and the other two iterations ran.
        Assert.Contains("1", ex.Message);
        Assert.Equal(3, handler.CallCount);
    }
```

This test is the reason `ForEachNode`'s referenceability was checked while the plan was written: it is public, in the same SDK assembly as `INodeContext`, so the test project reaches it without a new dependency. Should the wiring above not line up on first contact - the aggregate exception type, the root-context arguments or the iteration seeding - fix the wiring rather than dropping the test; if a genuine blocker appears (for instance the loop needing infrastructure the unit test project has no way to provide), fall back to asserting that `MeshAdapterPipelineExecutionException` is not an `OperationCanceledException`, quote the filter `catch (Exception e) when (iterationErrors is not null && e is not OperationCanceledException)` in the test comment, and say so in the handover.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestNodeTests`
Expected: compile error - `HttpErrorHandling` and `OnHttpError` do not exist.

- [ ] **Step 3: Add the enum and the property**

In `MakeHttpRequestNodeConfiguration.cs`:

```csharp
    /// <summary>
    /// What a failed request does to the pipeline.
    /// </summary>
    public enum HttpErrorHandling
    {
        /// <summary>Report the failure and stop this branch, leaving the execution successful.</summary>
        LogAndStop,

        /// <summary>Fail the execution, so a surrounding loop or the run itself reports it.</summary>
        Throw
    }
```

and in the configuration record:

```csharp
        /// <summary>
        /// How a failed request is answered. The default keeps the behaviour the node had before
        /// the option existed: the failure is logged and the following nodes are skipped, while
        /// the execution still succeeds. It governs runtime outcomes only - a configuration
        /// mistake always fails.
        /// </summary>
        [PropertyGroup("Connection", 10)]
        public HttpErrorHandling OnHttpError { get; set; } = HttpErrorHandling.LogAndStop;
```

- [ ] **Step 4: Route the node's request through the sender**

Replace the direct `httpClient.SendAsync(request)` block. The request construction moves into a factory so each attempt builds its own message, and the failure decision sits in one place:

```csharp
        try
        {
            using var response = await HttpRequestSender.SendAsync(httpClient,
                () => BuildRequest(dataContext, nodeContext, c, url, apiSettings),
                c.Retry ?? new HttpRetryOptions(), c.TimeoutSeconds, _timeProvider, nodeContext);

            await StoreResponseAsync(dataContext, nodeContext, c, response);
        }
        catch (MeshAdapterPipelineExecutionException e) when (c.OnHttpError == HttpErrorHandling.LogAndStop)
        {
            nodeContext.Error(e, "Error making HTTP request");
            return;
        }
        catch (Exception e) when (e is not MeshAdapterPipelineExecutionException)
        {
            // The net the node has always had, kept in every mode. Throw widens what fails the
            // execution to HTTP outcomes and to nothing else: a malformed response body or a header
            // the target refuses would otherwise start escaping from a node whose owner only
            // enabled paging.
            nodeContext.Error(e, "Error making HTTP request");
            return;
        }
```

`BuildRequest` is the existing header, body and content code moved into a private static method; `StoreResponseAsync` is the existing response-format handling moved out of the same method. Keep both behaviours exactly as they are - this task changes when a failure throws, not how a response is stored.

The order of the two catch clauses is what makes this work: the first sees the typed HTTP failures and only in `LogAndStop`, the second everything that is not a typed HTTP failure, in both modes. Under `Throw` a typed HTTP failure matches neither and leaves the node, which is the point.

- [ ] **Step 4b: Pin the retained net with a test**

```csharp
    [Fact]
    public async Task ProcessObjectAsync_ResponseHandlingFails_IsLoggedInBothModes()
    {
        // A response the storage step cannot handle is not an HTTP outcome, so OnHttpError does not
        // govern it: it stays reported rather than escaping, exactly as before the option existed.
        foreach (var mode in new[] { HttpErrorHandling.LogAndStop, HttpErrorHandling.Throw })
        {
            var config = new MakeHttpRequestNodeConfiguration
            {
                Method = "GET", Url = "https://host/api", TargetPath = "$.response",
                ResponseFormat = "Auto", OnHttpError = mode
            };
            var (dataContext, nodeContext, next, logger) =
                PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
            A.CallTo(() => dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._)).Throws(new InvalidOperationException("boom"));
            var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("plain text"));

            var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
            await node.ProcessObjectAsync(dataContext, nodeContext);

            A.CallTo(() => logger.Error(A<string>._, A<string>._, A<Exception>._, A<string>._,
                A<object[]>._)).MustHaveHappened();
        }
    }
```

If the faked `IDataContext.Set` overload used here does not match the one the storage step calls for a text response, adapt the fake to whichever overload it uses - the point of the test is an exception raised inside the response handling, not which call raises it.

- [ ] **Step 5: Run the full test class**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequest`
Expected: PASS, including every pre-existing test.

- [ ] **Step 6: Commit**

```bash
git add src/MeshAdapter.Sdk src/MeshNodes.Sdk tests/MeshAdapter.Sdk.Tests
git commit -m "AB#4846: let a pipeline choose whether a failed request fails the run"
```

---

### Task 5: Paging

**Files:**
- Modify: `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`
- Modify: `src/MeshAdapter.Sdk/Nodes/Transform/MakeHttpRequestNode.cs`
- Modify: `src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestNodeTests.cs`

**Interfaces:**
- Consumes: `HttpRequestSender.SendAsync`, `HttpErrorHandling` from Tasks 3 and 4.
- Produces: `HttpPagingOptions { string ItemsPath; string PageParameterName = "page"; string PageSizeParameterName = "pageSize"; int PageSize = 100; int FirstPageNumber = 1; bool StopOnShortPage = true; int MaxPages = 500; }` and the configuration property `Paging`.

- [ ] **Step 1: Write the failing tests**

```csharp
    private static MakeHttpRequestNodeConfiguration PagingConfig(HttpPagingOptions paging)
    {
        return new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api/article", TargetPath = "$.items",
            OnHttpError = HttpErrorHandling.Throw, Paging = paging
        };
    }

    private static string Page(params int[] ids)
    {
        return "{\"result\":[" + string.Join(",", ids.Select(i => $"{{\"id\":{i}}}")) + "]}";
    }

    [Fact]
    public async Task ProcessObjectAsync_Paging_CollectsEveryPageFlatAndInOrder()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Json(Page(3, 4)),
            SequencedHttpMessageHandler.Json(Page(5)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal("https://host/api/article?page=1&pageSize=2",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("https://host/api/article?page=3&pageSize=2",
            handler.Requests[2].RequestUri!.ToString());
        A.CallTo(() => dataContext.Set("$.items",
                A<JsonNode?>.That.Matches(n => n!.AsArray().Count == 5 &&
                                               n.AsArray()[0]!["id"]!.GetValue<int>() == 1 &&
                                               n.AsArray()[4]!["id"]!.GetValue<int>() == 5),
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_Paging_StopsOnEmptyPage()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Json(Page()));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingWithoutShortPageStop_WalksToTheEmptyPage()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, StopOnShortPage = false
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1)),
            SequencedHttpMessageHandler.Json(Page(2)),
            SequencedHttpMessageHandler.Json(Page()));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingFirstPageNumberZero_StartsAtZero()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, FirstPageNumber = 0
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains("page=0", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingKeepsAnExistingQuery()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Url = "https://host/api/salesOrder?status-eq=CONFIRMED";
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("https://host/api/salesOrder?status-eq=CONFIRMED&page=1&pageSize=2",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("{\"other\":[]}")]
    [InlineData("{\"result\":{\"id\":1}}")]
    [InlineData("not json at all")]
    public async Task ProcessObjectAsync_PagingUnusableItemsPath_FailsInsteadOfStopping(string body)
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(body));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("$.result", ex.Message);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingCapReached_FailsAndWritesNothing()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, MaxPages = 3
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        // A target that ignores the page parameter answers with the same full page forever.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1, 2)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Equal(3, handler.CallCount);
        Assert.Contains("3", ex.Message);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingWithoutItemsPath_Throws()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "", PageSize = 2 });
        config.OnHttpError = HttpErrorHandling.LogAndStop; // a configuration mistake throws anyway
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingRetriesOnePageInPlace()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Retry = new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"),
            SequencedHttpMessageHandler.Json(Page(3, 4)),
            SequencedHttpMessageHandler.Json(Page(5)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Page 2 was retried rather than skipped, and page 1 was not fetched again.
        Assert.Equal(4, handler.CallCount);
        Assert.Equal("https://host/api/article?page=2&pageSize=2",
            handler.Requests[2].RequestUri!.ToString());
        A.CallTo(() => dataContext.Set("$.items",
                A<JsonNode?>.That.Matches(n => n!.AsArray().Count == 5),
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingExhaustedOnOnePage_FailsTheWholeRun()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Retry = new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("{\"other\":[]}")]   // unusable itemsPath
    [InlineData("{\"result\":[{\"id\":1},{\"id\":2}]}")]   // full page forever, so the cap is hit
    public async Task ProcessObjectAsync_PagingFailureUnderDefault_IsQuietAndWritesNothing(string body)
    {
        // The paging failure paths follow OnHttpError like every other runtime outcome: with the
        // default they are reported and stop the branch instead of failing the execution.
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, MaxPages = 2
        });
        config.OnHttpError = HttpErrorHandling.LogAndStop;
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(body));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Error(A<string>._, A<string>._, A<Exception>._, A<string>._,
            A<object[]>._)).MustHaveHappened();
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingDefaults_AreTheDocumentedOnes()
    {
        var paging = new HttpPagingOptions { ItemsPath = "$.result" };

        Assert.Equal("page", paging.PageParameterName);
        Assert.Equal("pageSize", paging.PageSizeParameterName);
        Assert.Equal(100, paging.PageSize);
        Assert.Equal(1, paging.FirstPageNumber);
        Assert.True(paging.StopOnShortPage);
        Assert.Equal(500, paging.MaxPages);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestNodeTests`
Expected: compile error - `HttpPagingOptions` and `Paging` do not exist.

- [ ] **Step 3: Add the paging options**

```csharp
    /// <summary>
    /// Page-number paging over a collection endpoint. Absent means a single request. The property
    /// names are page-number specific so a cursor mode can be added later without renaming.
    /// </summary>
    public record HttpPagingOptions
    {
        /// <summary>
        /// Single-level path of the form "$.name" addressing the array inside one response, for
        /// example "$.result". Deeper addressing belongs to a downstream transformation.
        /// </summary>
        public string ItemsPath { get; set; } = "";

        /// <summary>Query parameter carrying the page number.</summary>
        public string PageParameterName { get; set; } = "page";

        /// <summary>Query parameter carrying the page size.</summary>
        public string PageSizeParameterName { get; set; } = "pageSize";

        /// <summary>Elements requested per page.</summary>
        public int PageSize { get; set; } = 100;

        /// <summary>Number of the first page; some APIs count from zero.</summary>
        public int FirstPageNumber { get; set; } = 1;

        /// <summary>
        /// Treat a page holding fewer elements than requested as the last one. Turn it off for an
        /// API that caps the page size server-side, where every page looks short.
        /// </summary>
        public bool StopOnShortPage { get; set; } = true;

        /// <summary>
        /// Upper bound on pages. Reaching it fails: a target that ignores the page parameter
        /// answers with the same page forever, and a silent stop would truncate the result.
        /// </summary>
        public int MaxPages { get; set; } = 500;
    }
```

and in the configuration record:

```csharp
        /// <summary>
        /// Collects every page of a paged endpoint into one flat array at the target path.
        /// </summary>
        [PropertyGroup("Data Mapping", 5)]
        public HttpPagingOptions? Paging { get; set; }
```

- [ ] **Step 4: Add the paging failure factories**

```csharp
    public static Exception HttpPagingItemsPathUnusable(INodeContext nodeContext, string itemsPath,
        int page)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The response for page {page} carries no array at '{itemsPath}'. " +
            "An empty array ends the walk; a missing or non-array value means the response shape changed.");
    }

    public static Exception HttpPagingCapReached(INodeContext nodeContext, int maxPages)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The paged request reached its limit of {maxPages} pages. " +
            "Raise maxPages if the collection really is that large, or check that the target honours " +
            "the page parameter - the result would otherwise be truncated silently.");
    }

    public static Exception HttpPagingItemsPathNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Paging needs an itemsPath naming the array inside one response.");
    }
```

- [ ] **Step 5: Implement the walk in the node**

Validate before the first request (a configuration mistake, so outside the `OnHttpError` translation):

```csharp
        if (c.Paging is { } paging && string.IsNullOrWhiteSpace(paging.ItemsPath))
        {
            throw MeshAdapterPipelineExecutionException.HttpPagingItemsPathNotSet(nodeContext);
        }
```

and inside the try, in place of the single send:

```csharp
            if (c.Paging is { } paging)
            {
                var collected = new JsonArray();
                var page = paging.FirstPageNumber;

                for (var walked = 0; walked < paging.MaxPages; walked++)
                {
                    var pageUrl = AppendQuery(url,
                        $"{paging.PageParameterName}={page}&{paging.PageSizeParameterName}={paging.PageSize}");

                    using var pageResponse = await HttpRequestSender.SendAsync(httpClient,
                        () => BuildRequest(dataContext, nodeContext, c, pageUrl, apiSettings),
                        c.Retry ?? new HttpRetryOptions(), c.TimeoutSeconds, _timeProvider, nodeContext);

                    var body = await pageResponse.Content.ReadAsStringAsync();
                    var items = ReadItems(body, paging.ItemsPath)
                                ?? throw MeshAdapterPipelineExecutionException.HttpPagingItemsPathUnusable(
                                    nodeContext, paging.ItemsPath, page);

                    foreach (var item in items)
                    {
                        collected.Add(item?.DeepClone());
                    }

                    if (items.Count == 0 || (paging.StopOnShortPage && items.Count < paging.PageSize))
                    {
                        dataContext.Set(c.TargetPath, collected, c.DocumentMode, c.TargetValueKind,
                            c.TargetValueWriteMode);
                        await next(dataContext, nodeContext);
                        return;
                    }

                    page++;
                }

                throw MeshAdapterPipelineExecutionException.HttpPagingCapReached(nodeContext, paging.MaxPages);
            }
```

with the two helpers:

```csharp
    private static string AppendQuery(string url, string query)
    {
        return url.Contains('?') ? $"{url}&{query}" : $"{url}?{query}";
    }

    private static JsonArray? ReadItems(string body, string itemsPath)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        // The path is the flat "$.name" form the pipeline uses for a response envelope; anything
        // deeper belongs to a downstream transformation rather than to the page walk.
        var name = itemsPath.StartsWith("$.", StringComparison.Ordinal) ? itemsPath[2..] : itemsPath;
        return parsed?[name] as JsonArray;
    }
```

If `ReadItems` needs to support a deeper path than one level, use the data context's JSONPath support rather than growing this helper - but only if a test demands it.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestNodeTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MeshAdapter.Sdk src/MeshNodes.Sdk tests/MeshAdapter.Sdk.Tests
git commit -m "AB#4846: collect every page of a paged endpoint into one array"
```

---

### Task 6: What an explicit null in a definition does

**Files:**
- Test: `tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestConfigurationDeserializationTests.cs` (create)
- Modify (only if a test demands it): `src/MeshNodes.Sdk/Transform/MakeHttpRequestNodeConfiguration.cs`, `src/MeshAdapter.Sdk/Nodes/Transform/MakeHttpRequestNode.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 5.
- Produces: no new type. It pins the behaviour a tenant definition can actually produce, which a C# object initializer cannot reach: a property initializer only applies when a key is **absent**, so an explicitly null key hands the node a null it never expects. That is the shape of mistake a null integer in a configuration entry caused once before, and the deserializer is the only place it shows up.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MeshAdapter.Sdk.Tests/Nodes/Transforms/MakeHttpRequestConfigurationDeserializationTests.cs
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MakeHttpRequestConfigurationDeserializationTests : NodeTestBase
{
    private static async Task<MakeHttpRequestNodeConfiguration> DeserializeAsync(string transformationYaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(MakeHttpRequestNode));
        var serializer = services.BuildServiceProvider()
            .GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + transformationYaml);
        return root.Transformations!.OfType<MakeHttpRequestNodeConfiguration>().Single();
    }

    [Fact]
    public async Task Deserialize_SectionsOmitted_UsesDocumentedDefaults()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
            """);

        Assert.Null(config.Paging);
        Assert.Null(config.TimeoutSeconds);
        Assert.Equal(HttpErrorHandling.LogAndStop, config.OnHttpError);
        Assert.Equal("Authorization", config.AuthHeaderName);
        Assert.Equal("", config.AuthHeaderValuePrefix);
        Assert.Equal(1, (config.Retry ?? new HttpRetryOptions()).MaxAttempts);
    }

    [Fact]
    public async Task Deserialize_ExplicitNullSections_DoNotReachTheNodeAsNull()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
                retry: null
                paging: null
                timeoutSeconds: null
                authHeaderName: null
                authHeaderValuePrefix: null
            """);

        // Whatever the deserializer does with a null key, the node must survive it: the values it
        // reads are either the documented defaults or a configuration error, never a null it
        // dereferences.
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"result\":\"ok\"}"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        var exception = await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.IsNotType<NullReferenceException>(exception);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Deserialize_ExplicitNullNumbersInsideSections_ResolveToDefaults()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
                retry:
                  maxAttempts: null
                  backoffBaseSeconds: null
                paging:
                  itemsPath: $.result
                  pageSize: null
                  firstPageNumber: null
                  maxPages: null
            """);

        var retry = config.Retry ?? new HttpRetryOptions();
        Assert.Equal(1, retry.MaxAttempts);
        Assert.Equal(1, retry.BackoffBaseSeconds);
        Assert.Equal(100, config.Paging!.PageSize);
        Assert.Equal(1, config.Paging.FirstPageNumber);
        Assert.Equal(500, config.Paging.MaxPages);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequestConfigurationDeserializationTests`
Expected: the first test passes; the others show what the deserializer really does with an explicit null.

- [ ] **Step 3: Make the node survive whatever the deserializer does**

Three outcomes are possible, and each has one correct answer:

1. The deserializer **rejects** an explicit null (strict, throws) - then a tenant cannot produce the situation. Change the affected tests into assertions that the deserialization fails with a message naming the property, and note the behaviour in the node's XML documentation.
2. The deserializer **keeps the initializer** - then the assertions already pass. Keep them as regression cover, since a later serializer change would break the node silently.
3. The deserializer **writes null** over the initializer - then make the affected properties nullable with a resolved default at the use site, exactly as `Retry` already is, and make `AuthHeaderName` / `AuthHeaderValuePrefix` fall back to `"Authorization"` and `""` when null. Adapt the assertions to the resolved values.

Whichever applies, the run ends with the node never dereferencing a null from a definition.

- [ ] **Step 4: Run the whole class again**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug --filter FullyQualifiedName~MakeHttpRequest`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MeshAdapter.Sdk src/MeshNodes.Sdk tests/MeshAdapter.Sdk.Tests
git commit -m "AB#4846: survive an explicitly null section in a pipeline definition"
```

---

### Task 7: Documentation and full verification

**Files:**
- Modify: `docs/developer-guide.md` (the `MakeHttpRequestNode` section)
- Modify: `CLAUDE.md` (the node list entry)
- Modify: `docs/test-concept.md` (the `MakeHttpRequestNode` entry)

**Interfaces:**
- Consumes: everything built in Tasks 1 to 5.
- Produces: the documentation a reviewer reads instead of the diff.

- [ ] **Step 1: Extend the developer guide**

In the `MakeHttpRequestNode` table add the new parameters, then a short prose block below it:

```markdown
| `ApiConfiguration` | string? | GlobalConfiguration entry supplying `baseUrl` and `apiKey` |
| `AuthHeaderName` / `AuthHeaderValuePrefix` | string | Header the key is sent in, and an optional scheme prefix |
| `TimeoutSeconds` | int? | Timeout per attempt; unset leaves the HTTP client default |
| `Retry` | HttpRetryOptions? | `MaxAttempts` (default 1, so no retry), `BackoffBaseSeconds` (default 1, waited after a failed attempt and doubling) |
| `Paging` | HttpPagingOptions? | Page-number walk collecting every page into one array |
| `OnHttpError` | enum | `LogAndStop` (default) or `Throw` |

> **Configured access:** with `ApiConfiguration` set, the URL is a path relative to the entry's
> `baseUrl` and the key travels in `AuthHeaderName` (`Authorization` by default, sent scheme-less
> unless `AuthHeaderValuePrefix` supplies one). An absolute URL is rejected in that mode, so a typo
> cannot send the key to another host. The entry is read when the pipeline is deployed, so a
> rotated key takes effect after a redeploy.

> **Paging:** `ItemsPath` is a single-level path of the form `$.name` naming the array inside one response. The walk stops on an empty page and,
> unless `StopOnShortPage` is turned off, on a page shorter than `PageSize`; a response without an
> array at `ItemsPath` and reaching `MaxPages` both fail rather than truncating the result quietly.
> All pages land as one flat array at `TargetPath`, written once the walk is complete.

> **Failures:** `OnHttpError` decides what a runtime failure means - `LogAndStop` reports it and
> skips the following nodes while the execution still succeeds, `Throw` fails the execution so a
> surrounding `ForEach@1` with `continueOnError` can isolate the item. Configuration mistakes
> always fail. Retries cover 5xx, 408, 429, network errors and timeouts; other statuses fail at
> once.
```

- [ ] **Step 2: Add the CLAUDE.md entry**

Replace the bare `MakeHttpRequestNode` bullet in the Transform node list with a one-paragraph entry in the style of the `SftpListNode` entry: what the four capabilities do, that each is inert unless configured, why the default failure mode stays as it is, that an absolute URL with a configured entry is refused, and that the typed failure is what makes `ForEach@1` isolation work.

- [ ] **Step 3: Update the test concept**

Extend the `MakeHttpRequestNode` entry in `docs/test-concept.md` with the new groups: configured access, retry and timeout, paging, failure semantics.

- [ ] **Step 4: Run the full suite in both configurations**

Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Debug`
Run: `dotnet test tests/MeshAdapter.Sdk.Tests/MeshAdapter.Sdk.Tests.csproj -c Release`
Expected: PASS in both, no skipped tests beyond those already skipped on `main`.

- [ ] **Step 5: Check the diff against the pre-PR rules**

Run: `git diff main...HEAD | grep -nP "[\x{2010}-\x{2015}]"` - expected: no matches.
Read the diff once as a reviewer would: no internal references or ticket shorthand in comments, English throughout, XML documentation on every new public member.

- [ ] **Step 6: Commit**

```bash
git add docs CLAUDE.md
git commit -m "AB#4846: document the paged, retrying HTTP node"
```

- [ ] **Step 7: Hand back for the review gate**

Report the diff summary and the test results. The review gate (Fable/max over the full diff, or the documented fallback) runs before any push, and the push and the PR need explicit approval.

---

## Self-Review

**Spec coverage:** configured access - Tasks 1 and 2; absolute URL rejection - Task 2; retry with the transient set including `TaskCanceledException`, the backoff sequence and the per-attempt timeout including a target that never answers - Task 3; `OnHttpError`, the retained net for everything that is not an HTTP outcome, and the isolation demonstrated through the real loop node - Task 4; paging with all four stop rules, the flat accumulation and the failure paths under the default mode - Task 5; what an explicit null in a definition does - Task 6; documentation and the full two-configuration run - Task 7. The spec's "verification before coding" is done and recorded in the spec itself.

**Placeholder scan:** no TBD or TODO; every code step carries the code it asks for. Three steps are deliberately conditional and name every branch: the `TimeProvider` test package in Task 3, the faked `Set` overload in Task 4, and the three possible deserializer behaviours in Task 6.

**Type consistency:** `HttpApiSettings`, `HttpApiSettingsResolver.Resolve`, `HttpRetryOptions`, `HttpPagingOptions`, `HttpErrorHandling`, `HttpRequestSender.SendAsync`, `CombineUrl`, `AppendQuery` and `ReadItems` are used under the same names in every task that references them. The node constructor gains `IMeshEtlContext` in Task 2 and is used with three arguments from there on. `Retry` is nullable from the moment it exists (Task 3) and is read as `Retry ?? new HttpRetryOptions()` at both use sites.

**Two things the executor does not need to re-derive**, both verified in source while this plan was written and recorded under Global Constraints: the backoff waits after a failed attempt (`base * 2^(attempt-1)`, so 1 s, 2 s, 4 s), and `ForEachNode` is referenceable from the test project, which is why Task 4 pins the isolation through the real loop rather than through the exception type.
