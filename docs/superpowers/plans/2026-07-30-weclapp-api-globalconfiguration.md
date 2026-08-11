# WeClappApi-GlobalConfiguration (Expand-Schritt) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WeClapp-Nodes können BaseUrl + API-Key aus einem Tenant-GlobalConfiguration-Eintrag (`apiConfiguration: WeClappApi`) beziehen; Inline-Werte bleiben als Back-Compat erhalten.

**Architecture:** Ein gemeinsamer Resolver (`WeClappConnectionSettingsResolver`, Spiegel von `SftpConnectionSettingsResolver`) wird vom Fetch-Trigger (via `ITriggerContext.GlobalConfiguration`) und den AR/BE-Write-Nodes (via neu injiziertem `IMeshEtlContext`) genutzt. Configuration gewinnt über Inline; gesetzte-aber-fehlende/halbe Configuration → `WeClappPipelineExecutionException` (fail-loud, kein stiller Fallback).

**Tech Stack:** .NET 10, xUnit, FakeItEasy (`A.Fake<...>`), `FakeHttpMessageHandler` (zeichnet `(Method, Url, AuthToken, Body)` auf), Solution `Octo.WeClappAdapter.slnx`.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-30-weclapp-api-globalconfiguration-design.md` — Fehler-Matrix dort ist verbindlich.
- Fehlertyp immer `WeClappPipelineExecutionException`; Meldungstexte Englisch (Repo-Konvention).
- Der Klartext-Key erscheint NIE in Log-Aufrufen oder Exception-Messages (nur der Eintragsname); Test-Literale wie `"cfg-key"` sind erlaubt.
- KEINE Änderungen an `pipelines/*.yaml` und `scripts/om_setup_lkv.ps1` (Expand-Schritt; Flip = Folgecommit außerhalb dieses Plans).
- Jeder Task endet mit grünem `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo` (Task 4 zusätzlich `-c DebugL`).
- Commits: Prefix `AB#4228: `, Ende `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Arbeitsverzeichnis = Worktree `C:/Users/martin-lt/Development/meshmakers/worktrees/octo-adapter-weclapp-apikey` (Branch `feature/ab4228-apikey-globalconfig`).

---

### Task 1: `WeClappConnectionSettings` + Resolver

**Files:**
- Create: `src/AdapterMeshWeClapp/Services/WeClappConnectionSettings.cs`
- Test: `tests/AdapterMeshWeClapp.Tests/Services/WeClappConnectionSettingsResolverTests.cs` (neu; Namespace-Muster der Nachbardatei `tests/AdapterMeshWeClapp.Tests/Nodes/DilosSftpWriteNodeTests.cs` übernehmen)

**Interfaces:**
- Consumes: `IGlobalConfiguration` (`Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes`; Methoden `IsDefined(string)`, `GetValue<T>(string)`), `WeClappPipelineExecutionException` (bestehend).
- Produces: `record WeClappConnectionSettings { string BaseUrl; string ApiKey }` (init, Default `""` — bewusst NICHT `required`, damit ein halber Tenant-Eintrag die klare Resolver-Meldung trifft statt einer Deserialisierungs-Exception) und Extension `WeClappConnectionSettings ResolveWeClappSettings(this IGlobalConfiguration, string? apiConfiguration, string? inlineBaseUrl, string? inlineApiKey)`.

- [ ] **Step 1: Failing Tests schreiben** (eine Testklasse, 7 Fälle = Fehler-Matrix + Happy Paths)

```csharp
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Services;

public class WeClappConnectionSettingsResolverTests
{
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    [Fact]
    public void Resolve_ConfigurationEntry_ReturnsSettingsFromGlobalConfiguration()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });

        var settings = _globalConfiguration.ResolveWeClappSettings("WeClappApi", null, null);

        Assert.Equal("https://cfg.weclapp.com/webapp/api/v1", settings.BaseUrl);
        Assert.Equal("cfg-key", settings.ApiKey);
    }

    [Fact]
    public void Resolve_ConfigurationWinsOverInline()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });

        var settings = _globalConfiguration.ResolveWeClappSettings("WeClappApi", "https://inline.example", "inline-key");

        Assert.Equal("cfg-key", settings.ApiKey);
    }

    [Fact]
    public void Resolve_ConfigurationSetButUndefined_FailsLoudWithUsesHint()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(false);

        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings("WeClappApi", "https://inline.example", "inline-key"));

        Assert.Contains("WeClappApi", ex.Message);
        Assert.Contains("Uses association", ex.Message);
    }

    [Theory]
    [InlineData("", "cfg-key")]
    [InlineData("https://cfg.weclapp.com/webapp/api/v1", "")]
    public void Resolve_ConfigurationEntryIncomplete_FailsLoud(string baseUrl, string apiKey)
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = baseUrl, ApiKey = apiKey });

        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings("WeClappApi", null, null));

        Assert.Contains("WeClappApi", ex.Message);
        Assert.Contains("baseUrl", ex.Message);
        Assert.Contains("apiKey", ex.Message);
    }

    [Fact]
    public void Resolve_InlineOnly_ReturnsInlineSettings()
    {
        var settings = _globalConfiguration.ResolveWeClappSettings(null, "https://inline.weclapp.com/webapp/api/v1", "inline-key");

        Assert.Equal("https://inline.weclapp.com/webapp/api/v1", settings.BaseUrl);
        Assert.Equal("inline-key", settings.ApiKey);
        A.CallTo(() => _globalConfiguration.IsDefined(A<string>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null, "inline-key")]
    [InlineData("https://inline.weclapp.com/webapp/api/v1", null)]
    [InlineData(null, null)]
    public void Resolve_NoConfigurationAndIncompleteInline_FailsLoud(string? baseUrl, string? apiKey)
    {
        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings(null, baseUrl, apiKey));

        Assert.Contains("apiConfiguration", ex.Message);
    }
}
```

- [ ] **Step 2: Tests laufen lassen — erwartet: Compile-Fehler** (Typ existiert nicht)

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo`
Expected: Build-FAIL (`WeClappConnectionSettings` unbekannt).

- [ ] **Step 3: Implementierung**

```csharp
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>WeClapp API access settings — resolved from a tenant GlobalConfiguration entry
/// (e.g. "WeClappApi") or from inline node configuration. Members are deliberately not
/// <c>required</c>: a half-configured tenant entry must reach the resolver's clear error
/// instead of failing deserialization.</summary>
public record WeClappConnectionSettings
{
    /// <summary>API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>API token (sent as "AuthenticationToken" header) — never log it.</summary>
    public string ApiKey { get; init; } = "";
}

/// <summary>
/// Shared resolution of the WeClapp API access settings — one validation for the fetch
/// trigger and both write-back nodes (mirror of <see cref="SftpConnectionSettingsResolver"/>).
/// A configured-but-missing or half-configured entry fails loud; there is no silent
/// fallback to a possibly stale inline key.
/// </summary>
public static class WeClappConnectionSettingsResolver
{
    public static WeClappConnectionSettings ResolveWeClappSettings(
        this IGlobalConfiguration globalConfiguration,
        string? apiConfiguration, string? inlineBaseUrl, string? inlineApiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiConfiguration))
        {
            if (!globalConfiguration.IsDefined(apiConfiguration))
            {
                throw new WeClappPipelineExecutionException(
                    $"Global configuration '{apiConfiguration}' is not defined for this pipeline " +
                    "— link the configuration entity to the pipeline (Uses association)");
            }

            var settings = globalConfiguration.GetValue<WeClappConnectionSettings>(apiConfiguration);
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new WeClappPipelineExecutionException(
                    $"Global configuration '{apiConfiguration}' must provide both 'baseUrl' and 'apiKey'");
            }

            return settings;
        }

        if (string.IsNullOrWhiteSpace(inlineBaseUrl) || string.IsNullOrWhiteSpace(inlineApiKey))
        {
            throw new WeClappPipelineExecutionException(
                "WeClapp access is not configured — set 'apiConfiguration' (recommended) " +
                "or inline 'baseUrl' + 'apiKey'");
        }

        return new WeClappConnectionSettings { BaseUrl = inlineBaseUrl, ApiKey = inlineApiKey };
    }
}
```

- [ ] **Step 4: Tests grün laufen lassen**

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo`
Expected: PASS (253 Bestand + 8 neue).

- [ ] **Step 5: Commit**

```bash
git add src/AdapterMeshWeClapp/Services/WeClappConnectionSettings.cs tests/AdapterMeshWeClapp.Tests/Services/WeClappConnectionSettingsResolverTests.cs
git commit -m "AB#4228: add WeClappConnectionSettings resolver (GlobalConfiguration, LkvSftp pattern)"
```

---

### Task 2: Fetch-Trigger nutzt den Resolver

**Files:**
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs` (Config-Record :20-25; `FetchOnceAsync` :172-205; Signaturen `FetchArticlesAsync` :207, `FetchOrdersAsync`, `FetchAllPagesAsync`, `GetWithRetryAsync` :357; Key-Verwendung :368; BaseUrl-Verwendung im URL-Bau in `FetchAllPagesAsync`)
- Test: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs` (3 neue Tests; bestehende bleiben unverändert = Back-Compat-Beweis)

**Interfaces:**
- Consumes: `ResolveWeClappSettings(...)` aus Task 1; `ITriggerContext.GlobalConfiguration` (existiert — von `LkvSftpE2eSmokeTests.cs:201` gefaked).
- Produces: Config-Properties `string? ApiConfiguration` (neu), `string? BaseUrl`, `string? ApiKey` (beide nicht mehr `required`); YAML-Feld `apiConfiguration`.

- [ ] **Step 1: Failing Tests schreiben** (in die bestehende Testklasse; `Configure(...)`-Helper und `CreateSut(...)` wiederverwenden; JSON-Antwortform exakt von `FetchOnce_ArticleMode_PagesUntilShortPageAndExecutesPerItem` übernehmen)

```csharp
private IGlobalConfiguration FakeWeClappApiConfiguration(string baseUrl, string apiKey)
{
    var globalConfiguration = A.Fake<IGlobalConfiguration>();
    A.CallTo(() => _context.GlobalConfiguration).Returns(globalConfiguration);
    A.CallTo(() => globalConfiguration.IsDefined("WeClappApi")).Returns(true);
    A.CallTo(() => globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
        .Returns(new WeClappConnectionSettings { BaseUrl = baseUrl, ApiKey = apiKey });
    return globalConfiguration;
}

[Fact]
public async Task FetchOnce_ApiConfiguration_UsesResolvedBaseUrlAndKey()
{
    var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(/* leere Ergebnisseite wie im Nachbartest */));
    var sut = CreateSut(handler);
    var config = Configure("article");
    config.ApiConfiguration = "WeClappApi";
    config.BaseUrl = null;
    config.ApiKey = null;
    FakeWeClappApiConfiguration("https://cfg.weclapp.com/webapp/api/v1", "cfg-key");

    await sut.FetchOnceAsync(_context);

    Assert.All(handler.Requests, r => Assert.StartsWith("https://cfg.weclapp.com/webapp/api/v1/", r.Url));
    Assert.All(handler.Requests, r => Assert.Equal("cfg-key", r.AuthToken));
    Assert.NotEmpty(handler.Requests);
}

[Fact]
public async Task FetchOnce_ApiConfigurationWinsOverInline()
{
    var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(/* leere Ergebnisseite wie im Nachbartest */));
    var sut = CreateSut(handler);
    var config = Configure("article"); // Configure setzt Inline-BaseUrl https://demo... + "test-key"
    config.ApiConfiguration = "WeClappApi";
    FakeWeClappApiConfiguration("https://cfg.weclapp.com/webapp/api/v1", "cfg-key");

    await sut.FetchOnceAsync(_context);

    Assert.All(handler.Requests, r => Assert.Equal("cfg-key", r.AuthToken));
}

[Fact]
public async Task FetchOnce_ApiConfigurationMissingEntry_FailsLoudWithoutHttp()
{
    var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("{}"));
    var sut = CreateSut(handler);
    var config = Configure("article");
    config.ApiConfiguration = "WeClappApi";
    var globalConfiguration = A.Fake<IGlobalConfiguration>();
    A.CallTo(() => _context.GlobalConfiguration).Returns(globalConfiguration);
    A.CallTo(() => globalConfiguration.IsDefined("WeClappApi")).Returns(false);

    var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));

    Assert.Contains("WeClappApi", ex.Message);
    Assert.DoesNotContain("cfg-key", ex.Message);
    Assert.Empty(handler.Requests);
}
```

- [ ] **Step 2: Laufen lassen — erwartet FAIL** (`ApiConfiguration` existiert nicht / Setter-Compile-Fehler)

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo`

- [ ] **Step 3: Implementierung (minimal)**

Config-Record (ersetzt :20-25):

```csharp
/// <summary>Name of the tenant GlobalConfiguration entry with the WeClapp access settings
/// ({ baseUrl, apiKey }, e.g. "WeClappApi" — shared with the write-back nodes). When set,
/// it takes precedence over the inline <see cref="BaseUrl"/>/<see cref="ApiKey"/>; the key
/// then lives once per tenant instead of in every pipeline definition.</summary>
public string? ApiConfiguration { get; set; }

/// <summary>WeClapp API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".
/// Optional when <see cref="ApiConfiguration"/> is set.</summary>
public string? BaseUrl { get; set; }

/// <summary>WeClapp API token (sent as "AuthenticationToken" header) — never hardcode or
/// log it. Optional when <see cref="ApiConfiguration"/> is set.</summary>
public string? ApiKey { get; set; }
```

`FetchOnceAsync` (nach dem EmitMode-Guard, vor `CreateClient`):

```csharp
var settings = context.GlobalConfiguration.ResolveWeClappSettings(
    config.ApiConfiguration, config.BaseUrl, config.ApiKey);
```

Dann `settings` als zusätzlichen Parameter durchreichen: `FetchArticlesAsync(http, config, settings, context, _timeProvider, cancellationToken)`, `FetchOrdersAsync(http, config, settings, context, cancellationToken)`, `FetchAllPagesAsync(http, config, settings, …)`, `GetWithRetryAsync(http, url, config, settings, cancellationToken)`; darin `config.ApiKey` → `settings.ApiKey` (:368) und `config.BaseUrl` → `settings.BaseUrl` (URL-Bau in `FetchAllPagesAsync`). KEINE weitere Logikänderung.

- [ ] **Step 4: Alle Tests grün**

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo`
Expected: PASS (Bestand + 3 neue; bestehende Tests unverändert grün = Inline-Back-Compat bewiesen).

- [ ] **Step 5: Commit**

```bash
git add src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs
git commit -m "AB#4228: resolve WeClapp fetch access via apiConfiguration (config wins over inline)"
```

---

### Task 3: AR/BE-Write-Nodes nutzen den Resolver

**Files:**
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappWriteNodeConfiguration.cs` (:13-18), `src/AdapterMeshWeClapp/Nodes/WeClappArWriteNode.cs` (:37-40 ctor, :57-58), `src/AdapterMeshWeClapp/Nodes/WeClappBeWriteNode.cs` (ctor analog, :58)
- Modify (Aufrufstellen): alle Treffer von `grep -rn "new WeClappArWriteNode(\|new WeClappBeWriteNode(" tests/` — bekannt: `tests/AdapterMeshWeClapp.Tests/LkvSftpE2eSmokeTests.cs:194,327` + Konstruktionen in `WeClappArWriteNodeTests.cs`/`WeClappBeWriteNodeTests.cs`
- Test: je 1 neuer Config-Pfad-Test in `WeClappArWriteNodeTests.cs` und `WeClappBeWriteNodeTests.cs`

**Interfaces:**
- Consumes: `ResolveWeClappSettings(...)` (Task 1); `IMeshEtlContext.GlobalConfiguration` (DI-Beweis: `DilosSftpWriteNode.cs:40-43` + Test-Fake `DilosSftpWriteNodeTests.cs:22-23`).
- Produces: Write-Node-Konstruktoren `(NodeDelegate next, ILogger<T> logger, IHttpClientFactory httpClientFactory, IMeshEtlContext etlContext)`; Config-Properties wie Task 2.

- [ ] **Step 1: Failing Test schreiben** (Muster der jeweiligen Testklasse übernehmen; hier der AR-Fall — BE analog mit dessen Fixture)

```csharp
[Fact]
public async Task Process_ApiConfiguration_UsesResolvedBaseUrlAndKey()
{
    // Fixture der Klasse verwenden; Node mit zusätzlichem etlContext konstruieren:
    var etlContext = A.Fake<IMeshEtlContext>();
    var globalConfiguration = A.Fake<IGlobalConfiguration>();
    A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
    A.CallTo(() => globalConfiguration.IsDefined("WeClappApi")).Returns(true);
    A.CallTo(() => globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
        .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });
    // Config wie im Happy-Path-Test der Klasse, aber: ApiConfiguration = "WeClappApi", BaseUrl = null, ApiKey = null

    // Act: ProcessObjectAsync mit dem Standard-Testdokument der Klasse

    Assert.All(handler.Requests, r => Assert.StartsWith("https://cfg.weclapp.com/webapp/api/v1/", r.Url));
    Assert.All(handler.Requests, r => Assert.Equal("cfg-key", r.AuthToken));
}
```

- [ ] **Step 2: Laufen lassen — erwartet FAIL/Compile-Fehler**

- [ ] **Step 3: Implementierung (minimal)**

`WeClappWriteNodeConfiguration` (:13-18 ersetzen — exakt dieselben drei Property-Blöcke wie in Task 2 Step 3, Verweistext „shared with the fetch trigger").

Beide Nodes: Primary Constructor um `Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext etlContext` erweitern (Vorbild `DilosSftpWriteNode.cs:40-43`); in `ProcessObjectAsync` vor der `WeClappApi`-Konstruktion:

```csharp
var settings = etlContext.GlobalConfiguration.ResolveWeClappSettings(
    config.ApiConfiguration, config.BaseUrl, config.ApiKey);
var api = new WeClappApi(httpClientFactory.CreateClient(nameof(WeClappArWriteNode)),
    settings.BaseUrl, settings.ApiKey, config.MaxRetries, config.RetryBackoffBaseSeconds);
```

Alle Test-Aufrufstellen: Parameter `A.Fake<IMeshEtlContext>()` ergänzen (Inline-Pfad ruft `IsDefined` nie auf — kein weiteres Fake-Setup nötig).

- [ ] **Step 4: Alle Tests grün**

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo`

- [ ] **Step 5: Commit**

```bash
git add src/AdapterMeshWeClapp/Nodes/ tests/AdapterMeshWeClapp.Tests/
git commit -m "AB#4228: resolve write-node WeClapp access via apiConfiguration (IMeshEtlContext DI)"
```

---

### Task 4: Format, beide Suiten, PR-Body

**Files:**
- Modify: nur was `dotnet format` anfasst
- Create (außerhalb des Repos): `C:/Users/martin-lt/Development/LKV-Vorbereitung/PR-BESCHREIBUNG-APIKEY-GLOBALCONFIG.md`

**Interfaces:** — (Abschluss-Task)

- [ ] **Step 1: Format**

Run: `dotnet format "Octo.WeClappAdapter.slnx"` — bei Änderungen: prüfen, committen (`AB#4228: dotnet format`).

- [ ] **Step 2: Beide Suiten**

Run: `dotnet test "Octo.WeClappAdapter.slnx" -c Debug --nologo` und `-c DebugL --nologo`
Expected: beide PASS, gleiche Testzahl.

- [ ] **Step 3: PR-Body schreiben** (LKV-Vorbereitung, Muster PR-BESCHREIBUNG-K1-K2-REDEPLOY.md): Was/Warum (Reimar-Punkt 3), Fehler-Matrix-Kurzform, Back-Compat-Nachweis (Bestandstests unverändert), Migrate/Contract-Folgeschritte inkl. Tenant-Eintrag `WeClappApi` + Uses-Association, ausdrücklich: KEIN YAML-/Skript-Change in diesem PR.

- [ ] **Step 4: Abschluss-Check** — `git log --oneline origin/main..HEAD` zeigt Spec + 3-4 Implementierungs-Commits; `git status` clean.

## Self-Review (erledigt beim Schreiben)

Spec-Abdeckung: Record+Resolver=T1, Fetch=T2, Write+DI=T3, Doku/Suiten=T4, YAML/Skript explizit ausgeschlossen ✓ · Platzhalter: die zwei „wie im Nachbartest"-Verweise sind bewusste Fixture-Übernahmen mit exakter Quellenangabe ✓ · Typkonsistenz: `ApiConfiguration`/`ResolveWeClappSettings`/`WeClappConnectionSettings` in allen Tasks identisch ✓
