# K1/K2 Redeploy-Härtung Implementation Plan (Go-Live-Plan v2, Phase 1) — **v3, umgesetzt in diesem Branch**

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **v2 (23.07.):** 3 Major + 5 Minor + 4 Nits aus der 4-Prüfer-Verifikation eingearbeitet (ApplyChanges@2 statt deprecated @1; TimeProvider als optionaler Ctor-Param nach echtem DilosRender-Muster; Silent-Empty-Prüfpunkt; Begründungs- und Pfadkorrekturen). Poll-Intervall 3600 s von Martin bestätigt.
> **v3 (23.07., nach unabhängiger 6-Agenten-Verifikation, Martins Go):** Prüfpunkt „unknown ckTypeId" GESCHLOSSEN — Verhalten ist fail-loud (Quellzitate in „Verifiziert"); Staging-Pflichtnachweis verschärft (Marker-Roundtrip: Folge-Polls nach erfolgreicher Lieferung liefern NICHTS); Task 5 aktualisiert auch den Phase-1-K1-Wortlaut im VORGEHENSPLAN (Datums-Bucket-Abweichung von Martin 23.07. abgesegnet). Detailkorrekturen: `FetchOnceAsync` ist internal (Tests via bestehendem InternalsVisibleTo); DRITTE Trigger-Ctor-Callsite `WeClappCustomerSmokeTests.cs:64` (2-Argument-Form, bleibt durch optionalen Parameter kompatibel); `ApplyChanges@2`: `entityUpdatesPath` UND `associationUpdatesPath` sind BEIDE nullable — fehlender/falscher Pfad ⇒ nur Warning „No update infos found", KEIN Fehler (deshalb der Marker-Roundtrip-Nachweis); dryRun-Beleg korrekt ApplyChangesNode2.cs:39-52.

**Goal:** Die as-Pipeline (einzige ungeschützte Liefer-Pipeline, 3 AS-Incidents) redeploy-fest machen: K1 = Liefer-Dedup-Gate in der Pipeline (max. 1 AS-Lieferung pro Wiener Kalendertag, Marker NACH Upload), K2 = `RunOnStart`-Option im Trigger (delay-first, kein Fetch beim Instanziieren).

**Architecture:** K2 ist eine Trigger-Änderung in `WeClappFetchTriggerNode` (delay-first-Loop bei `runOnStart: false`). K1 folgt exakt dem bewiesenen AI-Dedup-Gate-Muster (PR #6): `GetOrCreateRtEntitiesByType@1` probt einen Tages-Marker (`Industry.Logistics/ExportRun` mit `ExportKind`+`ExportDate`), `If@1` auf `ModOperation == Insert` gatet Render/Upload/Persist, `ApplyChanges@2` persistiert den Marker als LETZTEN Schritt (at-least-once). Der Trigger liefert dafür im Batch-Dokument `$.meta.exportKind` + `$.meta.exportDate` (Wiener Kalendertag) mit.

**Tech Stack:** .NET 10, xUnit + FakeItEasy, octo-communication-sdk-Pipeline-Nodes, octo-ckc (CK-Compile), DebugL-Feed (lokal repariert 22.07.; CI nutzt 3.4.\*-NuGets).

## Design-Entscheidungen (Review-Punkte, v2-korrigiert)

1. **Datums-Bucket statt „LastAsExportAt < 20 h" — gewählt, weil einfacher und drift-frei, NICHT weil die 20-h-Variante unmöglich wäre.** Machbar wäre sie: `GetRtEntitiesByType@1` schreibt komplette Entities inkl. Attributwerten in den Datenkontext (GetRtEntitiesByTypeNode.cs:34), und ein Cutoff-FieldFilter (`GreaterEqualThan` + `comparisonValuePath` auf ein Trigger-emittiertes `now−20h`) könnte das Marker-Alter direkt in der Query prüfen. Der Tages-Bucket ist trotzdem die bessere Wahl: kein dynamischer Cutoff, kalendertag-genau (deckt sich mit dem Golden Precedent Nachtlauf 02:02), Probe und Marker nutzen denselben Wert, If@1-Literale sind produktionstreu testbewiesen (AiExportGateTests). Randverhalten dokumentiert in Task 4 Step 6 (zwei Lieferungen < 2 h über die Tagesgrenze möglich — akzeptiert).
2. **Marker-Typ `ExportRun` kommt in `Industry.Logistics` 1.1.0** (Monorepo, additiver Minor-Bump — Regeln: `../octo-construction-kit-engine/docs/ck-semver-rules.md`, das CK-Repo selbst hat KEIN docs/; der CI-Gate `validate-ck-versions` validiert den Bump ohnehin maschinell, Achtung Exit-Code bei Violation = −6/250 → auf `-ne 0` gaten): „ein Export-/Lieferlauf eines Datenbestands" ist generische Logistik-Domäne (wiederverwendbar für die spätere Billbee-Migration); die Werte („AS") kommen aus dem Adapter — analog Carrier-Enum. **Fallback, falls Review das als adapterspezifisch einstuft:** identischer Typ in einem neuen Adapter-CK-Paket (Mehraufwand: Paket + Katalog + Tenant-Import).
3. **as-Poll-Intervall 86400 → 3600 s (Martin-Entscheid 23.07.):** Mit Gate ist häufiges Pollen liefer-seitig gefahrlos (max. 1×/Tag); stündlich verhindert die Starvation-Falle (delay-first + häufige Redeploys würden den Tageslauf sonst ewig verschieben). **Ehrliche Kosten:** wegen `enrichSupplySources: true` zieht JEDER Poll zusätzlich den kompletten articleSupplySource-Bestand („the most expensive call of the poll", WeClappFetchTriggerNodeTests.cs:104-105) — real bis zu 24×2 Pulls/Tag, nicht „24 leichte GETs". Alternative bei API-Last-Bedenken: 21600 s (4×/Tag, Lieferverzögerung nach Redeploy ≤ 6 h).
4. `ExportKind` als Trigger-Konfigurationswert (Default „AS", nur im Batch-Modus emittiert) — der Trigger bleibt DILOS-frei, das Pipeline-YAML bestimmt die Bedeutung.
5. **`runOnStart` wird in ALLEN drei WeClappFetch-YAMLs explizit gesetzt** (as=false, ck=true, ai=true — Spec-Wortlaut Plan v2 Phase 1), nicht nur über den Property-Default.

## Verifiziert (Quellen-Belege aus der adversarischen Prüfung — beim Implementieren NICHT erneut prüfen)

- FieldFilter-Mehrfachkriterien = **UND**: `RtEntityQueryOptions.Create()` default `LogicalOperators.And` (octo-construction-kit-engine/src/Runtime.Contracts/Repositories/Query/RtEntityQueryOptions.cs:82; MongoDB-Kombination FieldFilterResolver.cs:167). Der frühere ExportKey-Fallback ist obsolet.
- GetOrCreate bei Miss = **query-only**: frische OctoObjectId + `UpdateKind.Insert` nur in den Datenkontext, KEIN Repository-Write (GetOrCreateRtEntitiesByTypeNode.cs:40-56); Query-Cache greift bei der Probe nicht (cached nur bei navigationPairs > 0, TenantRepository.cs:761-763).
- FieldFilter-Element = `FieldFilterWithPathDto` (AttributePath/Operator/ComparisonValue[Literal, hat Vorrang]/ComparisonValuePath); Node-Mapping unterstützt Equals…AnyEq (`Equals` sicher).
- `NodeDefinitionRoot.Triggers` existiert; YamlPipelineConfigurationSerializer deserialisiert Trigger-Configs typisiert und STRIKT (unbekannte Properties werfen) → Contract-Test-Pins auf Trigger-Properties funktionieren.
- `ViennaTime.Zone` existiert bereits öffentlich (src/Lkv.WeClapp.Core/ViennaTime.cs:12, `FindSystemTimeZoneById("Europe/Vienna")`); `FixedTimeProvider(DateTimeOffset utcNow)` (tests/.../FixedTimeProvider.cs:4-7).
- dryRun ist Pipeline-Execution-Mode (keine Node-Option): DilosSftpWrite überspringt nur den Netz-Upload (DilosSftpWriteNode.cs:84-90), ApplyChanges persistiert im dryRun NICHTS (ApplyChangesNode2.cs:39-52, RecordDryRunIntent → next → return VOR jedem ApplyChangesAsync) → Marker bleibt ungeschrieben, Gate bleibt offen, Phase-2-dryRun vergiftet den Tages-Bucket nicht (Nebenwirkung: jeder Poll loggt einen Would-upload).
- **Unknown ckTypeId = fail-loud (Prüfpunkt 23.07. GESCHLOSSEN):** `TenantRepository.GetRtEntitiesByTypeAsync` ruft VOR jedem Mongo-Query-Aufbau `GetCkTypeGraphAsync` (octo-construction-kit-engine-mongodb/…/TenantRepository.cs:681-687); bei unbekanntem Typ wirft die Kette — RuntimeRepositoryBase.cs:586-593 `RuntimeRepositoryException.RtCkTypeIdDoesNotExistInCache`, praktisch schon CkCache.cs:346-359 `CkCacheException.RtCkTypeIdNotFound` („Ensure that the corresponding construction kit library is imported and loaded", CkCacheException.cs:39-43); `GetOrCreateRtEntitiesByTypeNode` rethrowt im catch (AbortTransaction + Error + `throw`, Z.69-78) ⇒ Pipeline-Fehler, KEINE Lieferung. Silent-Empty existiert auf diesem Pfad nicht. Randfall ebenfalls fail-loud: Tenant-Cache fehlt komplett ⇒ `CkCacheException.CkCacheNotFound` (CkCacheService.cs:122-124).
- **ApplyChanges@2-Nullable-Falle (Warum Marker-Roundtrip-Nachweis Pflicht ist):** `entityUpdatesPath`/`associationUpdatesPath` sind beide `string?` OHNE Required-Attribut (ApplyChangesNodeConfiguration2.cs:15+21); fehlender/ins Leere zeigender Pfad ⇒ leere Liste ⇒ nur Warning „No update infos found" (ApplyChangesNode2.cs:123) — Gate bliebe still offen. Der Contract-Test pinnt die YAML-Struktur, NICHT das Persist-Verhalten ⇒ Staging-Roundtrip (Task 5 Step 3) ist der Beweis.
- CK: csproj bindet den ConstructionKit-Ordner komplett ein (csproj:19-21, keine weitere Registrierung); `${System}/Entity` löst transitiv über Basic auf (Kompilat: dependencies Basic-2.0.2 + System-2.2.2); neuer Typ mit required-Attributen ohne Defaults = trotzdem Minor (Diff steigt nur in gematchte Typen ab; Test `NewType_IsMinor`).

## Global Constraints

- Repo `octo-adapter-weclapp`: Branch NEU von `origin/main` (`feature/ab4228-redeploy-hardening`); aktueller Checkout `chore/smoke-tests-customer-account` (PR #8) NICHT anfassen.
- Commits im Format `AB#4228: <message>` (Wiki-Guideline); TDD strikt red→green; `TreatWarningsAsErrors` aktiv; vor jedem Commit `dotnet format --verify-no-changes` grün.
- Tests laufen mit `dotnet test -c DebugL` (lokaler Feed; bei Feed-Bruch: octo-sdk → communication-sdk → Konsument neu bauen).
- KEIN `git push` ohne Martins explizites Go (Governance). Secrets nie in Repo/Chat/Logs.
- Zeitzone für den Datums-Bucket: bestehender Helfer `ViennaTime.Zone` (Lkv.WeClapp.Core) — dieselbe Quelle wie die DilosRender-Dateinamen.
- CK-Monorepo-Task (Task 2) ist ein SEPARATER PR in `octo-construction-kit`; die Adapter-Unit-Tests hängen NICHT daran (ckTypeId ist im Config-DTO ein String, kein Tenant-Zugriff) — nur der spätere Tenant-Deploy (Phase 2) braucht `Industry.Logistics-1.1.0` importiert (Reihenfolge: `ImportFromCatalog` VOR Re-Import des as-YAML).

---

### Task 1: K2 — `RunOnStart`-Option im Trigger (delay-first)

**Files:**
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs` (Konfiguration Z. 16–55, StartAsync Z. 75–114)
- Test: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs`

**Interfaces:**
- Consumes: bestehende Test-Harness (`CreateSut`, `Configure` [nur `entity` Pflicht, gibt Config zurück], `FakeHttpMessageHandler`, `_executedDocuments`).
- Produces: `WeClappFetchTriggerNodeConfiguration.RunOnStart` (bool, Default `true`) — **Task 4** setzt `runOnStart: false` im as-YAML und `runOnStart: true` explizit in ck-/ai-YAML.

- [ ] **Step 1: Failing Test schreiben** (in `WeClappFetchTriggerNodeTests.cs`):

```csharp
[Fact]
public async Task StartAsync_RunOnStartFalse_DoesNotFetchBeforeFirstInterval()
{
    var config = Configure("article");
    config.RunOnStart = false;
    config.PollingIntervalSeconds = 3600;
    var handler = new FakeHttpMessageHandler((_, _) =>
        FakeHttpMessageHandler.Json("""{"result":[]}"""));
    var sut = CreateSut(handler);

    await sut.StartAsync(_context);
    await Task.Delay(250);
    await sut.StopAsync(_context);

    Assert.Empty(handler.Requests);          // delay-first: kein API-Call beim Start
    Assert.Empty(_executedDocuments);        // und keine Pipeline-Execution
}
```

**Kein zweiter neuer Test:** Das Sofort-Fetch-Verhalten bei Default `RunOnStart = true` pinnt bereits der bestehende `StartAndStop_TerminateCleanly` (Z. 332–345, `Assert.NotEmpty(handler.Requests)` nach 50 ms Fixdelay). Dort das 50-ms-Fixdelay durch das robuste Poll-Muster ersetzen:

```csharp
var deadline = DateTime.UtcNow.AddSeconds(5);
while (handler.Requests.Count == 0 && DateTime.UtcNow < deadline)
{
    await Task.Delay(10);
}
```

- [ ] **Step 2: Fail verifizieren** — `dotnet test -c DebugL --filter "FullyQualifiedName~WeClappFetchTriggerNodeTests"` → `RunOnStart` existiert nicht = Compile-Fehler (rot).

- [ ] **Step 3: Minimal implementieren** — Property in die Konfiguration (nach `PollingIntervalSeconds`):

```csharp
/// <summary>When false, the polling loop delays FIRST and fetches only after the first
/// interval — a (re)deploy then never triggers an immediate fetch/delivery (P2
/// redeploy determinism; the as pipeline sets false). Default true keeps the
/// fetch-first behavior for idempotent pipelines (ck) and gated ones (ai).</summary>
public bool RunOnStart { get; set; } = true;
```

und in `StartAsync` vor der `while`-Schleife (innerhalb `Task.Run`):

```csharp
if (!config.RunOnStart)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), token);
    }
    catch (OperationCanceledException)
    {
        return;
    }
}
```

- [ ] **Step 4: Grün verifizieren** — gleicher Testlauf: neuer Test + angepasster `StartAndStop_TerminateCleanly` PASS, Rest der Suite unverändert grün.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "AB#4228: add RunOnStart option to WeClappFetch (delay-first loop for redeploy determinism)"`

---

### Task 2: CK — `ExportRun`-Typ (SEPARATES Repo `octo-construction-kit`, eigener PR)

**Files:**
- Create: `src/ConstructionKits/Octo.Sdk.Packages.Industry.Logistics/ConstructionKit/types/exportRun.yaml`
- Create: `src/ConstructionKits/Octo.Sdk.Packages.Industry.Logistics/ConstructionKit/attributes/exportRun.yaml`
- Modify: `src/ConstructionKits/Octo.Sdk.Packages.Industry.Logistics/ConstructionKit/ckModel.yaml` (Z. 10: `modelId: Industry.Logistics-1.0.0` → `-1.1.0`)

**Interfaces:**
- Produces: CK-Typ `Industry.Logistics/ExportRun` mit Attributen `ExportKind` (String) + `ExportDate` (String) — Task 4 referenziert beide wörtlich im Pipeline-YAML.

- [ ] **Step 1: Branch** — `git -C ../octo-construction-kit checkout -b feature/ab4228-logistics-export-run origin/main` (vorher `git fetch`).
- [ ] **Step 2: `types/exportRun.yaml`** (Spiegel von `types/stock.yaml`-Stil):

```yaml
$schema: https://schemas.meshmakers.cloud/construction-kit-elements.schema.json
types:
- typeId: ExportRun
  derivedFromCkTypeId: ${System}/Entity        # outbound export/delivery log
  description: "One outbound export/delivery run of a dataset (e.g. article master), keyed by kind and calendar day. Doubles as the delivery-dedup marker: at most one delivery per kind and day."
  attributes:
  - id: ${this}/ExportKind
    name: ExportKind
  - id: ${this}/ExportDate
    name: ExportDate
```

- [ ] **Step 3: `attributes/exportRun.yaml`** (Semantik als `description:` — landet in der generierten Doku, YAML-Kommentare nicht):

```yaml
$schema: https://schemas.meshmakers.cloud/construction-kit-elements.schema.json
attributes:
- id: ExportKind
  valueType: String
  description: "Kind of export/delivery run (e.g. article master export); the adapter supplies the value."
- id: ExportDate
  valueType: String
  description: "Calendar day (yyyy-MM-dd) in the delivery time zone — bucket key of the dedup marker, not a timestamp."
```

- [ ] **Step 4: Version-Bump** — `ckModel.yaml`: `modelId: Industry.Logistics-1.1.0`. Additiver neuer Typ = Minor (Regeln + Klassifizierer: `../octo-construction-kit-engine/docs/ck-semver-rules.md`, Test `NewType_IsMinor`; der CI-Gate `validate-ck-versions` läuft VOR dem Build und erzwingt das ohnehin).
- [ ] **Step 5: Compile-Beweis** — `../octo-construction-kit-engine/bin/Release/net10.0/octo-ckc.exe -c Compile -p <Paket-Ordner> -o <out>` → **Exit 0** (nichts als fertig melden ohne grünen Compile). Alternativ Monorepo-Projekt-Build (`dotnet build -c Release` im Paket).
- [ ] **Step 6: Commit** — `AB#4228: add Industry.Logistics/ExportRun (delivery-dedup marker, 1.1.0)`. PR-Anlage + Push NUR nach Martins Go; Reviewer wie beim CK-PR #25 (Gerry).

---

### Task 3: K1a — Trigger emittiert Gate-Metadaten im Batch-Modus

**Files:**
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs` (Konfiguration + `FetchArticlesAsync` Batch-Zweig Z. 214–232; Konstruktor Z. 67–69; `using Lkv.WeClapp.Core;` ergänzen)
- Test: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs`

**Interfaces:**
- Consumes: `FixedTimeProvider(DateTimeOffset utcNow)` (tests/.../FixedTimeProvider.cs:4-7) und den EXISTIERENDEN öffentlichen Helfer `ViennaTime.Zone` (Lkv.WeClapp.Core) — nichts extrahieren, nichts registrieren.
- Produces: Batch-Dokument-Form `{ "items": [...], "meta": { "exportKind": "<config>", "exportDate": "yyyy-MM-dd" } }`; neue Config-Property `ExportKind` (string, Default `"AS"`).

- [ ] **Step 1: Failing Test** (FixedTimeProvider so wählen, dass UTC→Wien den Tag wechselt — beweist die TZ-Logik):

```csharp
[Fact]
public async Task FetchOnce_BatchMode_EmitsExportMarkerMeta_ViennaCalendarDay()
{
    Configure("article", emitMode: "Batch");
    var handler = new FakeHttpMessageHandler((_, _) =>
        FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"}]}"""));
    // 22:30 UTC = 00:30 Wien (CEST, UTC+2) am FOLGETAG → exportDate muss 2026-07-24 sein
    var sut = CreateSut(handler, new FixedTimeProvider(
        new DateTimeOffset(2026, 7, 23, 22, 30, 0, TimeSpan.Zero)));

    await sut.FetchOnceAsync(_context);

    var document = Assert.Single(_executedDocuments);
    Assert.Equal("AS", document!["meta"]!["exportKind"]!.ToString());
    Assert.Equal("2026-07-24", document["meta"]!["exportDate"]!.ToString());
    Assert.Single(document["items"]!.AsArray());
}
```
(`CreateSut`-Signatur um `TimeProvider? timeProvider = null` erweitern und an den Konstruktor durchreichen. PerItem-Modus bleibt meta-frei: bestehende PerItem-/Batch-Count-Tests dürfen sich NICHT ändern — sie greifen `meta` nicht an.)

- [ ] **Step 2: Fail verifizieren** (Compile-Fehler: Konstruktor kennt keinen TimeProvider-Parameter).
- [ ] **Step 3: Implementieren** — **Konstruktor EXAKT nach DilosRender-Muster als OPTIONALEN Parameter** (DilosRenderNode.cs:42-44; es gibt KEINE TimeProvider-DI-Registrierung in Program.cs, und `PipelineChainIntegrationTests.cs:69/171` sowie die DRITTE Callsite `WeClappCustomerSmokeTests.cs:64` konstruieren den Trigger direkt mit 2 Argumenten — der optionale Parameter hält alle drei kompatibel, kein Program.cs-Touch. `FetchOnceAsync` ist **internal**, der Test erreicht sie über das bestehende InternalsVisibleTo):

```csharp
public class WeClappFetchTriggerNode(
    ILogger<WeClappFetchTriggerNode> logger,
    IHttpClientFactory httpClientFactory,
    TimeProvider? timeProvider = null) : ITriggerPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

Config-Property:

```csharp
/// <summary>Marker kind emitted as $.meta.exportKind in Batch mode — the delivery-dedup
/// gate keys its per-day CK marker (Industry.Logistics/ExportRun) on it. Only used
/// with emitMode Batch.</summary>
public string ExportKind { get; set; } = "AS";
```

Batch-Zweig (`FetchArticlesAsync`) — Dokument erweitern (`FetchArticlesAsync` ist `static`, Z. 175: den berechneten `exportDate`-String oder den TimeProvider als Parameter durchreichen — kleinste Änderung wählen):

```csharp
var viennaNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), ViennaTime.Zone);
await ExecutePipelineAsync(context, new JsonObject
{
    ["items"] = items,
    ["meta"] = new JsonObject
    {
        ["exportKind"] = config.ExportKind,
        ["exportDate"] = viennaNow.ToString("yyyy-MM-dd"),
    },
});
```

- [ ] **Step 4: Grün verifizieren** — Suite komplett (`dotnet test -c DebugL`), inkl. unveränderter PerItem-/Enrichment-/Chain-Tests.
- [ ] **Step 5: Commit** — `AB#4228: emit per-day export marker meta in WeClappFetch batch mode (Vienna calendar day)`

---

### Task 4: K1b — AS-Liefer-Gate im Pipeline-YAML + Tests

**Files:**
- Modify: `pipelines/weclapp-articles-to-as.yaml`
- Modify: `pipelines/weclapp-articles-to-ck.yaml` + `pipelines/weclapp-orders-to-ai.yaml` (nur `runOnStart: true` explizit, Spec-Wortlaut)
- Create: `tests/AdapterMeshWeClapp.Tests/AsExportGateTests.cs`
- Kein Nachziehen in `PipelineYamlContractTests.cs` nötig (verifiziert: keine as-spezifischen Pins; der repo-weite Contract 1 [attributeValueType-Pflicht, strikte Deserialisierung aller pipelines/*.yaml, Walk() steigt in If-Container ab] greift automatisch und wird vom neuen Block bestanden).

**Interfaces:**
- Consumes: `Industry.Logistics/ExportRun` (Task 2), `$.meta.exportKind`/`$.meta.exportDate` (Task 3), `RunOnStart` (Task 1).
- Produces: gehärtetes as-YAML; Contract-Test, der Gate + Ordnung + Trigger-Optionen pinnt.

- [x] **Step 1: Rest-Verifikation — GESCHLOSSEN (23.07., v3):** Unbekannter ckTypeId = **fail-loud** (Beleg-Kette in der „Verifiziert"-Sektion oben). Der Silent-Empty-Fehlmodus existiert nicht; ins YAML kommt der fertige Kommentar (siehe Step 4), in den PR-Body die Beleg-Kette (Task 5). Die früheren Prüfpunkte (FieldFilter-UND, Miss-Verhalten) sind ebenfalls source-verifiziert — siehe „Verifiziert"-Sektion, nicht wiederholen.
- [ ] **Step 2: Failing Contract-Test `AsExportGateTests.cs`** — Spiegel von `AiExportGateTests.OrdersToAiYaml_…` (gleicher Serializer, gleiches `FindRepoFile`-Muster, gleicher `RegisterNodeConfiguration`-Block):

```csharp
[Fact]
public async Task ArticlesToAsYaml_GatesDeliveryOnDailyMarker_AndPersistsOnlyAfterUpload()
{
    // Deserialisierung wie in AiExportGateTests (Z. 99-115).
    // Trigger-Pins (K2 + Starvation-Schutz):
    var trigger = Assert.Single(root.Triggers!.OfType<WeClappFetchTriggerNodeConfiguration>());
    Assert.False(trigger.RunOnStart);
    Assert.Equal(3600, trigger.PollingIntervalSeconds);
    Assert.Equal("Batch", trigger.EmitMode);
    Assert.Equal("AS", trigger.ExportKind);

    var top = root.Transformations?.ToList() ?? new List<NodeConfiguration>();
    // Lookup (query-only) außerhalb des Gates, nichts Lieferndes/Persistierendes davor:
    var probe = Assert.Single(top.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
    Assert.Equal("Industry.Logistics/ExportRun", probe.CkTypeId);
    Assert.NotNull(probe.FieldFilters);
    Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportKind");
    Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportDate");
    Assert.DoesNotContain(top, n => n is DilosRenderNodeConfiguration);
    Assert.DoesNotContain(top, n => n is DilosSftpWriteNodeConfiguration);
    Assert.DoesNotContain(top, n => n is ApplyChangesNodeConfiguration2);
    Assert.DoesNotContain(top, n => n is CreateUpdateInfoNodeConfiguration);

    // Ein Gate mit den testbewiesenen Literalen (Insert = heute noch nicht geliefert):
    var gate = Assert.Single(top.OfType<IfNodeConfiguration>());
    Assert.Equal("$.rt.asExportRunModOperation", gate.Path);
    Assert.Equal(CompareOperator.Equal, gate.Operator);
    Assert.Equal(AttributeValueTypesDto.Enum, gate.ValueType);
    Assert.Equal((int)UpdateKind.Insert, Convert.ToInt32(gate.Value));

    // Im Gate: render → upload → Marker-CreateUpdateInfo → ApplyChanges@2 als LETZTER Schritt:
    var children = gate.Transformations!.ToList();
    var renderIndex = children.FindIndex(n => n is DilosRenderNodeConfiguration);
    var uploadIndex = children.FindIndex(n => n is DilosSftpWriteNodeConfiguration);
    var markerIndex = children.FindIndex(n => n is CreateUpdateInfoNodeConfiguration);
    var persistIndex = children.FindIndex(n => n is ApplyChangesNodeConfiguration2);
    Assert.True(renderIndex >= 0);
    Assert.True(uploadIndex > renderIndex, "Upload nach Render, im Gate");
    Assert.True(markerIndex > uploadIndex, "Marker-Update NACH dem Upload (at-least-once)");
    Assert.Equal(persistIndex, children.Count - 1);
}
```
(Die If-Semantik-Tests aus `AiExportGateTests` [`PrepareGate`] NICHT duplizieren — gleiche Literale sind dort produktionstreu bewiesen; nur der Pfad-String unterscheidet sich und ist über den YAML-Pin abgedeckt.)

- [ ] **Step 3: Fail verifizieren** — Test rot (YAML hat weder Gate noch runOnStart; `RunOnStart`/`ExportKind` existieren nach Task 1+3 bereits — kein Compile-Problem).
- [ ] **Step 4: YAML umbauen** — `weclapp-articles-to-as.yaml`, Trigger-Abschnitt:

```yaml
triggers:
  - type: WeClappFetch@1
    baseUrl: https://REPLACE-TENANT.weclapp.com/webapp/api/v1
    apiKey: ${WECLAPP_API_KEY}
    entity: article
    emitMode: Batch               # one execution per poll: { items: [ all articles ], meta: {...} }
    exportKind: AS                # keys the per-day delivery marker (Industry.Logistics/ExportRun)
    pageSize: 100
    # K2: delay-first — a (re)deploy never triggers an immediate poll (3 AS incidents were
    # exactly this mechanism). K1 (gate below) makes frequent polling delivery-safe: max ONE
    # delivery per Vienna calendar day, so hourly polling only fights starvation
    # (delay-first + frequent redeploys would otherwise postpone the daily run forever).
    # Cost note: each poll also pulls articleSupplySource (EK enrichment) — accepted 23.07.
    runOnStart: false
    pollingIntervalSeconds: 3600
```

Transformations-Abschnitt (Render+Upload wandern INS Gate, Konfigurationswerte unverändert):

```yaml
transformations:
  # --- K1 delivery-dedup gate: at most ONE AS delivery per Vienna calendar day.
  # GetOrCreate only QUERIES (fresh id + ModOperation=Insert on miss — source-verified,
  # GetOrCreateRtEntitiesByTypeNode.cs:40-56); multiple fieldFilters combine as AND
  # (RtEntityQueryOptions default LogicalOperators.And). The marker entity is persisted
  # by ApplyChanges@2 as the LAST step, i.e. only after a successful upload
  # (at-least-once — a crash between upload and persist re-delivers on the next poll).
  # Unknown ckTypeId (tenant without Industry.Logistics 1.1.0) => the platform THROWS before
  # querying (CkCache: "Ensure that the corresponding construction kit library is imported")
  # => pipeline error, NO delivery. Fail-loud verified 2026-07-23 against platform sources.
  - type: GetOrCreateRtEntitiesByType@1
    description: Probe today's AS export marker (query only — see gate note)
    ckTypeId: Industry.Logistics/ExportRun
    fieldFilters:
      - attributePath: ExportKind
        operator: Equals
        comparisonValuePath: $.meta.exportKind
      - attributePath: ExportDate
        operator: Equals
        comparisonValuePath: $.meta.exportDate
    rtIdTargetPath: $.rt.asExportRunRtId
    ckTypeIdTargetPath: $.rt.asExportRunCkTypeId
    modOperationPath: $.rt.asExportRunModOperation

  - type: If@1
    description: Deliver only when no marker for today exists (Insert = not delivered today)
    path: $.rt.asExportRunModOperation
    operator: Equal
    value: 0                    # UpdateKind.Insert — same proven literal as the AI gate
    valueType: Enum
    transformations:
      - type: DilosRender@1
        description: Render ALL article lines into one AS file (golden precedent, one file per run)
        mode: AS
        path: $.items
        targetPath: $.dilosAs
        fileNameTargetPath: $.dilosAsFileName   # AS<yyyyMMddHHmmss>.txt, Vienna local time

      - type: DilosSftpWrite@1
        description: Deliver the AS file to the LKV SFTP root as ISO-8859-1
        serverConfiguration: LkvSftp        # SAME tenant GlobalConfiguration as the return path
        remoteDirectory: /
        fileNamePath: $.dilosAsFileName
        path: $.dilosAs

      # attributeValueType is REQUIRED per update (staging finding 2026-07-16 — without it
      # the update is silently dropped and the marker never persists).
      - type: CreateUpdateInfo@1
        description: Today's export marker — written ONLY after a successful upload
        path: $.meta
        rtIdPath: $.rt.asExportRunRtId
        ckTypeId: Industry.Logistics/ExportRun
        updateKindPath: $.rt.asExportRunModOperation
        attributeUpdates:
          - attributeName: ExportKind
            attributeValueType: String
            valuePath: $.meta.exportKind
          - attributeName: ExportDate
            attributeValueType: String
            valuePath: $.meta.exportDate
        targetPath: $.updates
        targetValueWriteMode: Append

      # ApplyChanges@1 is [NodeDeprecated] — @2 is the current node (the AI gate uses it
      # too); associationUpdatesPath is optional/null-safe, the AS gate has no associations.
      - type: ApplyChanges@2
        description: Persist the marker (export dedup) — LAST step by contract
        entityUpdatesPath: $.updates
```

Dazu in `weclapp-articles-to-ck.yaml` und `weclapp-orders-to-ai.yaml` im Trigger-Block ergänzen (Spec-Wortlaut „ck/ai=true", selbstdokumentierend):

```yaml
    runOnStart: true              # fetch-first is wanted here: ck is idempotent, ai is gated
```

- [ ] **Step 5: Grün verifizieren** — neuer Contract-Test PASS; Gesamtsuite grün (Contract 1 der PipelineYamlContractTests deckt die neuen CreateUpdateInfo-Updates automatisch ab).
- [ ] **Step 6: Kopf-Kommentar des as-YAML aktualisieren** — Gate + runOnStart erwähnen (Muster: AI-Dedup-Gate/PR #6) und das Randverhalten dokumentieren: Lieferzeitpunkt pendelt sich auf den ERSTEN Poll nach Wien-Mitternacht ein (00:00–01:00, deckt sich mit Golden Precedent 02:02); über die Tagesgrenze sind zwei Lieferungen in kurzem Abstand möglich (bewusst akzeptiert). **Commit** — `AB#4228: gate the AS delivery on a per-day CK export marker (K1, at-least-once after upload)`

---

### Task 5: Abschluss — Suite, Format, Doku, PR-Body

**Files:**
- Modify: `readme.md`/`CLAUDE.md` des Repos (nur falls dort Pipeline-/Trigger-Verhalten beschrieben ist → RunOnStart + Gate ergänzen)
- Create: `C:\Users\martin-lt\Development\LKV-Vorbereitung\PR-BESCHREIBUNG-K1-K2-REDEPLOY.md` (PR-Body-Entwurf)

- [ ] **Step 1:** `dotnet test -c DebugL` UND `dotnet test -c Debug` (3.4.\*-Feed wie CI) — beide komplett grün; Zahl der Tests notieren.
- [ ] **Step 2:** `dotnet format --verify-no-changes` → clean.
- [ ] **Step 3:** PR-Body-Entwurf schreiben mit: Problem (3 AS-Incidents, P2-Verletzung), Lösung K1+K2 (inkl. der 5 Design-Entscheidungen oben), Invarianten-Tabelle NEU (as ✅ Gate+RunOnStart), Test-Beweise, Verweis CK-PR (`Industry.Logistics-1.1.0`), Deploy-Hinweis (Tenant braucht 1.1.0 via `ImportFromCatalog` VOR Re-Import des as-YAML) — **plus drei Betriebs-Notizen:** (a) **Unknown-ckTypeId = fail-loud (source-verifiziert 23.07., Beleg-Kette aus „Verifiziert"-Sektion zitieren)** → Phase-2/3-Checklistenpunkt „Staging-Nachweis: as-Pipeline stoppt OHNE importiertes 1.1.0 fail-loud ohne Lieferung" bleibt als E2E-Bestätigung; (b) **Marker-Roundtrip-PFLICHTNACHWEIS (v3, wichtigster Staging-Punkt):** „2. und 3. Poll NACH erfolgreicher Lieferung liefern NICHTS" — deckt den Warning-only-Fehlmodus von ApplyChanges@2 bei fehlerhaftem `entityUpdatesPath` ab (Nullable-Falle, siehe „Verifiziert"), den der Contract-Test strukturell nicht beweisen kann; Blast-Radius-Begründung: bei offenem Gate liefert die Pipeline stündlich ans Partner-SFTP (bis zu 24 Dateien/Tag). Optional konservativ: erste Prod-Woche 21600 s, Hochschalten auf 3600 s nach beobachtetem 1×/Tag-Verhalten (Entscheid Martin in Phase 2); (c) **dryRun-Verträglichkeit**: dryRun ist Execution-Mode, DilosSftpWrite UND ApplyChanges überspringen — Marker wird nicht persistiert, Gate bleibt offen, Phase-2-dryRun-Validierung kollidiert nicht mit K1 (aber jeder Poll erzeugt einen Would-upload-Log).
- [ ] **Step 4 (nach Martins Go):** Push + PR öffnen (`AB#4228`, Reviewer gemäß Absprache), CK-PR ebenfalls. Danach LKV-Doc `VORGEHENSPLAN-GO-LIVE-V2-2026-07-22.md` in ZWEI Punkten aktualisieren: (1) Invarianten-Tabelle auf ✅; (2) **Phase-1-K1-Wortlaut korrigieren** („CK-Anker LastAsExportAt, <20h-Gate" → Datums-Bucket `ExportRun` — Abweichung von Martin 23.07. explizit abgesegnet), sonst bleibt Doku-Drift.

## Self-Review v2 (durchgeführt, inkl. adversarischer 4-Prüfer-Verifikation)

- Spec-Abdeckung: K1 (Gate, Marker nach Upload, at-least-once) ✅ Task 3+4; K2 (RunOnStart, delay-first; as=false, ck/ai=true — jetzt EXPLIZIT in allen drei YAMLs) ✅ Task 1+4; „Nicht anfassen" (DilosFileFetch, SDK-PollingService) ✅ kein Task berührt sie; Exit-Kriterien ✅ Task 5. Redeploy-Testabdeckung: Unit-Ebene beweist „Start ohne Fetch/Execution"; der End-to-End-Redeploy-Beweis liegt bewusst in Phase 3 (Staging-Redeploy-Provokation, Plan v2) — Repo-Präzedenz: Plattform-Built-ins werden am Tenant erprobt, nicht gemockt. Stufe 2 Content-Hash bewusst NICHT geplant (Plan-v2-Entscheidung 3: YAGNI).
- Alle vormals stillen Annahmen sind jetzt entweder source-verifiziert („Verifiziert"-Sektion) oder als expliziter Prüfpunkt markiert (einzig offen: Unknown-ckTypeId-Verhalten, Task 4 Step 1).
- Typkonsistenz: `$.meta.exportKind`/`$.meta.exportDate`/`$.rt.asExportRun*`/`ExportRun`-Attribute/`ApplyChangesNodeConfiguration2` in Task 3, 4 und Contract-Test identisch.
