# Trigger Separation (AB#4228 / G2) Implementation Plan — v3

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **v2 (2026-08-05):** revised after an adversarial 3-reviewer source-check (3 blockers, 12 majors fixed — RT import format, adapter binding via `Executes`, contract-test serializer registrations, ForEach target/merge paths, Confirm-node scope, HttpClient gzip, ck element shape, cron stagger, generator switch, post-merge follow-up). Findings log: session chronicle 05.08.
>
> **v3 (2026-08-05 evening):** revised after a second, independent 5-agent source review (ultracode `wf_39b56265`). **BLOCKER fixed:** importing PipelineTrigger entities schedules NOTHING — cron schedules materialize only in `TriggerManagementService.UpdateScheduleAsync`, whose sole callers are tenant start (`DefaultConfigurationCreatorService.cs:107`) and POST /PipelineTrigger/deploy = `octo-cli -c DeployTriggers` (`PipelineTriggerController.cs:53`); Tasks 1/8/10 and Follow-up 0 now carry the `DeployTriggers` step. Majors fixed: Task-3 pending-delete-retry parity split, always-seed-empty-arrays rule (ForEach `PathMustBeArray`), Task-5 registration shrink (`AddMeshDataPipelineNodes` already covers the SDK trigger config) + `FindRepoFile` prefix, Task-6 per-test reality (`Walk` helper already descends), ForEach merge-order + maxDop-default cautions, `dotnet restore` precondition (local assets pinned 3.4.73), Task 10 targets `readme.md`, Task-8 Skip-guards, TZ/engine wording (communication-services pod TZ, Hangfire, Quartz-style 6-field), Enabled-flip lifecycle.

**Goal:** Replace the two custom polling trigger nodes (`WeClappFetch@1`, `DilosFileFetch@1`) with the platform's cron mechanism (`PipelineTrigger` entity + passive `FromPipelineTriggerEvent@1` trigger), moving fetch logic into regular step nodes — behavior-preserving, staging-only, without touching the prod go-live pin.

**Architecture:** Each of the 5 pipelines gets passive triggers (`FromPipelineTriggerEvent@1` cron + `FromExecutePipelineCommand@1` manual). Fetch logic moves into new transform nodes (`WeClappFetchStep@1`, `DilosFileFetchStep@1` + terminal `DilosFileConfirm@1`). Per-item fan-out becomes `ForEach@1` (child `transformations`, `maxDegreeOfParallelism: 1`). Cron schedules live in `System.Communication/PipelineTrigger` RT entities emitted by the generator behind a new opt-in switch; activation is a separate explicit step (`octo-cli -c DeployTriggers`).

**Tech Stack:** .NET 10, OctoMesh MeshAdapter SDK 3.4.x, xUnit + FakeItEasy, PowerShell generator (`LKV-Vorbereitung/scripts-prod/build-rt-weclapp.ps1`), octo-cli.

## Global Constraints

- **PUSH/PR/comments ONLY after Martin's explicit OK.** Local commits are fine. All work on branch `feature/ab4228-trigger-separation` (created in Task 2 Step 0, off main `74354de`).
- **Prod stays pinned to tag `r3.4.74`.** This work merges after review and reaches staging only via a later train. **Runbook S5 generates the prod RT-YAML from a `r3.4.74` worktree with `-RepoDir <worktree>` — never from main** (Task 10).
- Repo artifacts in **English**; no AI footer in PR bodies; commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- TDD; both `Debug` and `DebugL` suites green before any commit touching `src/`. Test command: `dotnet test --project tests/AdapterMeshWeClapp.Tests -c Debug` (and `-c DebugL`; same for `tests/Lkv.WeClapp.Core.Tests`).
- K1 delivery gate, AI dedup gate, `dryRun`/`deleteAfterSuccess` SAFE defaults: **untouched**.
- No octo-cli against clusters without Martin's GO for that step. **`ImportRt` upsert flag is `-r` WITHOUT a value.** Never `GetAdapter -id`. Never print credentials.
- Verified platform facts (2026-08-05, source-first; re-verified by the v3 5-agent review): `FromPipelineTriggerEvent@1` subscribes `octo::bot::pipeline-trigger-{tenant}-{pipelineRtId}` (lowercased) and calls `ExecuteAsync` directly (`FromPipelineTriggerEventNode.cs`); ships in NuGet `Meshmakers.Octo.MeshAdapter.Nodes` (namespace `Meshmakers.Octo.MeshAdapter.Nodes.Trigger`), registered in the adapter via `AddOctoMeshAdapter` → `AddMeshDataPipelineNodes`. `FromExecutePipelineCommand@1`, `ForEach@1` and `Logger@1` are base-serializer-registered (net10). **Scheduling lifecycle (v3 BLOCKER insight): importing PipelineTrigger entities schedules NOTHING.** Schedules materialize only in `TriggerManagementService.UpdateScheduleAsync` — callers: tenant start (`DefaultConfigurationCreatorService.cs:107`) and POST /PipelineTrigger/deploy = `octo-cli -c DeployTriggers` (`PipelineTriggerController.cs:53`). That call is a tenant-wide remove-then-add admitting only `System/Enabled=true` triggers, regardless of the target pipeline's DeploymentState; an Enabled flip via re-import bites only at the next rebuild (`DeployTriggers`/`UndeployTriggers`/tenant restart). Engine = Hangfire (Quartz-STYLE 6-field seconds-first), TZ = the communication-services pod's local TZ stamped at schedule creation (UTC by cluster convention; Task 1 verifies empirically), MisfirePolicy = Skip — downtime ticks are never replayed. Exceptions inside the trigger handler are caught and logged — next tick retries.

## Cron and rtId map (fixed values — Tasks 1 and 8)

| Pipeline | Pipeline rtId | Today | Cron (UTC) | PipelineTrigger rtId | Parent DataFlow |
|---|---|---|---|---|---|
| as | `aa4228000000000000000021` | 3600 s + K1 gate | `0 10 * * * ?` | `aa4228000000000000000031` | Delivery `aa4228000000000000000002` |
| ck | `aa4228000000000000000022` | 86400 s | `0 7 2 * * ?` (daily 02:07 UTC) | `aa4228000000000000000032` | Delivery |
| ai | `aa4228000000000000000023` | 900 s | `0 0/15 * * * ?` | `aa4228000000000000000033` | Delivery |
| ar | `41530481350b2481b6a2d777` | 900 s | `0 5/15 * * * ?` (:05 :20 :35 :50) | `aa4228000000000000000034` | Return `aa4228000000000000000001` |
| be | `86b660d02e85a3bb3d4e27a6` | 3600 s | `0 25 * * * ?` | `aa4228000000000000000035` | Return |

Stagger verified collision-free: as :10, be :25, ar :05/:20/:35/:50, ai :00/:15/:30/:45 overlap ai:00↔ck02:07 avoided; ar:20 vs be:25 disjoint. Interval parity 1:1 with today (as stays hourly — K1, not cron, enforces one delivery per Vienna day).

## Behavior-parity notes (read before Task 2)

1. Triggers today seed one execution per document via `context.ExecuteAsync(options, document)`. Step nodes write the same shapes into the data context; per-item chains run under `ForEach@1`.
2. **ForEach iteration semantics (source-verified, `ForEachNode.cs`):** each iteration runs in a **child context with parent fallback** — reads of paths not set in the child fall through to the parent; the item is seeded **only at `keyPath`**. Writes stay in the iteration context and are discarded after the iteration (except what `mergePath` collects). **DB-writing nodes (`ApplyChanges@1` — the ck YAML's actual version — and `@2`) are unaffected** — both write through `TenantRepository`, not the context (verified: AI marker persistence works inside iterations).
3. **Error isolation change (accepted):** an exception in a child aborts the whole tick; next cron tick retries. Business rejects in `WeClappArWrite` (404 order / already shipped) are dead-letter-logged, not thrown — they do NOT abort (verified `WeClappArWriteNode.cs:64-72,186-190`).
4. Ordering: `maxDegreeOfParallelism: 1` on every ForEach (AR name order; fetch sorts Ordinal by name).
5. `DilosFileFetch` cross-poll memory moves into a DI **singleton** (Task 3). A pod restart clears it → keep-mode re-logs files once; delete-mode unaffected.
6. **No start-tick anymore:** deploys no longer fire polls. Härtetest T3/T6 expectations change **only for charts containing this work** (Task 10 keeps 3.4.74 wording intact).
7. **Merged ForEach results are unordered** (ConcurrentBag) — even with `maxDegreeOfParallelism: 1` the `$.loopResult` order is unspecified. No converted pipeline consumes it (source-checked) and none may in future.
8. `ExternalReceivedDateTime` is no longer stamped on executions (today the trigger sets it; `FromPipelineTriggerEvent` does not) — execution-record metadata only; nothing in these pipelines reads it.
9. Business-reject scope: only the two named AR rejects (404 order / already shipped) are dead-lettered; every other exception aborts the tick per note 3 (accepted).

## Canonical ForEach block (use exactly this shape in Tasks 6/7)

```yaml
  - type: ForEach@1
    description: per-item chain, one former execution per element
    iterationPath: $.orders          # ck: $.articles | ar/be: $.files
    keyPath: $.current
    mergePath: $.current             # aligned with keyPath — default $.key would collect null
    targetPath: $.loopResult         # NEVER omit: default "$" REPLACES the document root.
                                     # Merge-result ORDER is unspecified (ConcurrentBag) — nothing may read $.loopResult.
    maxDegreeOfParallelism: 1        # NEVER omit: default 0 = Environment.ProcessorCount (parallel!)
    transformations:
      # former chain, with the item segment replaced: $.item → $.current.item etc.
```

**Array rule (all fetch steps):** ALWAYS seed the iteration array, even empty (`[]`) — a missing/non-array `iterationPath` aborts the tick with `PathMustBeArray`; `[]` no-ops gracefully.

---

### Task 1: Live-fire PipelineTrigger proof on staging ⛔ GO-gated (Martin)

Closes the two remaining secondary-evidence points (send-side queue prefix, TZ) empirically. Runs on the deployed **Mesh Adapter** (`670000000000000000000002`) — no adapter code involved.

**Files:**
- Create: `C:\Users\martin-lt\Development\LKV-Vorbereitung\scratch\rt-triggerprobe-staging.yaml` (temp, not committed)

**Interfaces:**
- Consumes: context `staging-1_lkv`; octo-cli at `C:\Users\martin-lt\Development\meshmakers\octo-cli\bin\Release\net10.0\win-x64\octo-cli.exe`
- Produces: evidence (two firings + UTC times) appended to `TRIGGER-TRENNUNG-DESIGN-2026-08-03.md`

- [ ] **Step 1: Write the probe YAML** — format and bindings mirror `octo-construction-kit/src/Samples/simulator/rt-dataflow-scheduled-trigger.yaml` exactly (`entities:` top-level; **the PIPELINE binds the adapter via `System.Communication/Executes`; the DataFlow has NO associations and NO Enabled attribute**):

```yaml
$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
dependencies:
  - System.Communication
entities:
  - rtId: dd4228000000000000000001
    ckTypeId: System.Communication/DataFlow
    attributes:
      - id: System/Name
        value: TriggerProbe DataFlow (temp)
  - rtId: dd4228000000000000000002
    ckTypeId: System.Communication/Pipeline
    associations:
      - roleId: System/ParentChild
        targetRtId: dd4228000000000000000001
        targetCkTypeId: System.Communication/DataFlow
      - roleId: System.Communication/Executes
        targetRtId: "670000000000000000000002"
        targetCkTypeId: System.Communication/Adapter
    attributes:
      - id: System/Name
        value: TriggerProbe Pipeline (temp)
      - id: System/Enabled
        value: true
      - id: System.Communication/IsDebuggingEnabled
        value: false
      - id: System.Communication/PipelineDefinition
        value: >-
          triggers:
            - type: FromPipelineTriggerEvent@1
          transformations:
            - type: Logger@1
              description: Trigger probe fired
              message: TriggerProbe fired
  - rtId: dd4228000000000000000003
    ckTypeId: System.Communication/PipelineTrigger
    associations:
      - roleId: System/ParentChild
        targetRtId: dd4228000000000000000001
        targetCkTypeId: System.Communication/DataFlow
      - roleId: System.Communication/Triggers
        targetRtId: dd4228000000000000000002
        targetCkTypeId: System.Communication/Pipeline
    attributes:
      - id: System/Name
        value: TriggerProbe Cron (temp)
      - id: System/Enabled
        value: true
      - id: System.Bot/CronExpression
        value: "0 * * * * ?"
```

- [ ] **Step 2 (⛔ GO):** `UseContext staging-1_lkv` → `LogIn -in` → `ImportRt -f <probe.yaml> -w` → deploy via `octo-cli -c DeployDataFlow -id dd4228000000000000000001` (command exists: `DeployDataFlowCommand.cs`, arg `-id`); if it errors, deploy via Studio (Communication → DataFlows → TriggerProbe → Deploy). Record which path worked. **Then activate the schedule: `octo-cli -c DeployTriggers`** — importing the PipelineTrigger entity alone schedules nothing (sole schedule writers: tenant start + this endpoint). The rebuild is tenant-wide but admits only Enabled=true triggers; the probe is staging-1's only PipelineTrigger, so nothing else is touched.
  ⚠️ Do NOT fall back to deploying the WeClapp adapter workload: that would start ALL its enabled pipelines (ck/ai with `runOnStart: true` → immediate real WeClapp fetch on an unpinned chart). If the probe cannot run on the Mesh Adapter, STOP and consult Martin.
- [ ] **Step 3 (cluster read):** wait ≥ 2 min → `GetPipelineExecutions -id dd4228000000000000000002 -j` → expect ≥ 2 executions ~60 s apart; record `StartedAt` UTC minute alignment (TZ proof).
- [ ] **Step 4 (⛔ GO, cluster write — teardown):** re-import the probe YAML with the **Pipeline and PipelineTrigger** `System/Enabled: false` (the DataFlow has no Enabled) using `octo-cli -c ImportRt -f <probe.yaml> -r -w` (**`-r` without a value**) → **`octo-cli -c DeployTriggers`** (rebuild WITHOUT the now-disabled trigger — the Enabled flip alone does not touch the live Hangfire schedule) → `octo-cli -c UndeployDataFlow -id dd4228000000000000000001` (removes the probe pipeline's consumer; command has no `-y`) → verify no execution over the next 2 minutes → then remove the three entities via Studio. ⚠️ Never leave an Enabled=true probe behind: every tenant restart re-materializes its schedule (`DefaultConfigurationCreatorService.cs:107`).
- [ ] **Step 5:** append evidence to `TRIGGER-TRENNUNG-DESIGN-2026-08-03.md` under a dated heading.

### Task 2: `WeClappFetchStep@1` transform node

- [ ] **Step 0: Create the work branch:** `git checkout -b feature/ab4228-trigger-separation` (off main `74354de`), then `dotnet restore` — the on-disk assets are pinned to SDK 3.4.73 from the last restore (versions float `3.4.*` via `Directory.Build.props`; the cache already holds 3.4.74).

**Files:**
- Create: `src/AdapterMeshWeClapp/Nodes/WeClappFetchStepNode.cs`
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs` (extract shared fetch statics into `internal static class WeClappFetchCore` in the same file; both nodes call them — no duplication; note: today they are `private static`, widen to `internal`)
- Test: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchStepNodeTests.cs` (reuse `FakeHttpMessageHandler`, `FixedTimeProvider` from this folder)

**Interfaces:**
- Consumes: `WeClappFetchCore.FetchAllPagesAsync(HttpClient, <config>, string entity, string additionalQuery, CancellationToken)` + article enrichment from `FetchArticlesAsync` (generalize the config parameter type while extracting).
- **HttpClient:** `httpClientFactory.CreateClient(nameof(WeClappFetchTriggerNode))` — **reuse the existing client name**: gzip `AutomaticDecompression` is registered per-name in `Program.cs:32-44`; a new name would silently lose decompression (WeClapp serves gzip) and only fail on staging.
- Produces: node `WeClappFetchStep@1`; config `WeClappFetchStepNodeConfiguration : NodeConfiguration` (pattern: `WeClappWriteNodeConfiguration.cs:11`; same fields as trigger config minus `PollingIntervalSeconds`/`RunOnStart`); class `WeClappFetchStepNode(NodeDelegate next, IHttpClientFactory httpClientFactory, ILogger<WeClappFetchStepNode> logger, TimeProvider? timeProvider = null) : IPipelineNode` with `ProcessObjectAsync(IDataContext, INodeContext)`, calling `await next(dataContext, nodeContext)` at the end. Document shapes (root writes):
  - `entity: article, emitMode: Batch` → `$.items` (enriched array) + `$.meta` (`exportKind`, `exportDate` = Vienna date) — byte-identical to today. **0-article case: still seed `$.items = []` + `$.meta`** (today the trigger skips the execution entirely; the empty-batch behavior of the downstream chain is asserted by a dedicated test and re-proven in the post-merge Härtetest).
  - `entity: salesOrder` → `$.orders` = array of `{ "item": <order>, "customer": <customer|null> }`.
  - `entity: article, emitMode: PerItem` (ck) → `$.articles` = array of **`{ "item": <enriched article> }`** — wrapped exactly like today's per-execution document, so the child-path rule is uniform.
  - **Empty-input rule (all modes): ALWAYS seed the array, even `[]`** — downstream `ForEach@1` throws `PathMustBeArray` on a missing array; `[]` no-ops (see Array rule at the top).

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] public async Task BatchArticleFetch_SeedsItemsAndMeta_AtRoot()      // 1 article page → $.items.Count==1, $.meta.exportKind=="AS"
[Fact] public async Task BatchArticleFetch_ZeroArticles_SeedsEmptyItems()  // empty page → $.items==[] and $.meta present
[Fact] public async Task OrderFetch_SeedsOrdersArray_ItemAndCustomerKeys() // 2 orders → $.orders.Count==2, each has item+customer
[Fact] public async Task PerItemArticleFetch_WrapsEachArticleInItemKey()   // ck shape: $.articles[0].item != null
[Fact] public async Task OrderFetch_ZeroOrders_SeedsEmptyOrdersArray()     // $.orders == [] (ForEach PathMustBeArray guard)
[Fact] public async Task PerItemArticleFetch_ZeroArticles_SeedsEmptyArray()// $.articles == []
[Fact] public async Task UnknownEntity_ThrowsWeClappPipelineExecutionException()
```

- [ ] **Step 2:** run → FAIL (type missing). **Step 3:** implement. **Step 4:** suites Debug+DebugL green. **Step 5:** commit `feat(AB#4228): add WeClappFetchStep@1 transform (fetch logic out of the trigger)`.

### Task 3: `DilosFileFetchStep@1` + `DilosFileConfirm@1` + singleton state

**Files:**
- Create: `src/AdapterMeshWeClapp/Nodes/DilosFileFetchStepNode.cs`
- Create: `src/AdapterMeshWeClapp/Nodes/DilosFileConfirmNode.cs`
- Create: `src/AdapterMeshWeClapp/Services/DilosFileFetchState.cs` (singleton; two locked `HashSet<string>`; API: `WasKeptOnServer`, `MarkKeptOnServer`, `HasPendingDelete`, `MarkPendingDelete`, `ClearPendingDelete`, `IntersectWith(currentKeys)`)
- Modify: `src/AdapterMeshWeClapp/Program.cs` (register singleton)
- Test: `tests/AdapterMeshWeClapp.Tests/Nodes/DilosFileFetchStepNodeTests.cs` (reuse fakes from `DilosFileFetchTriggerNodeTests.cs`)

**Interfaces:**
- Port the per-file loop from `DilosFileFetchTriggerNode.cs:150-212` (list+filter 150-158, loop 162-212).
- `DilosFileFetchStep@1` config = trigger config minus `PollingIntervalSeconds`. Emits `$.files` = name-ordered (Ordinal) array of
  `{ "fileName", "content", "fullPath", "key", "lastWriteTimeUtc" }` — `key` = precomputed FileKey (`{Name}|{Length}|{LastWriteTimeUtc.Ticks}`, see `DilosFileFetchTriggerNode.cs:215-216`), `fullPath` for deletion; **always seeds `$.files`, even `[]`** (no files → empty array, ForEach no-ops). Honors `minFileAgeSeconds` and keep-mode skip via the singleton. **Delete split (exact parity with today):** the step itself RETRIES pending deletes — a file whose key `HasPendingDelete` is deleted during listing WITHOUT being emitted or re-executed (mirror of `DilosFileFetchTriggerNode.cs:176-181`; `ClearPendingDelete` on success). First-time deletes of freshly processed files belong exclusively to `DilosFileConfirm@1`. A pod restart clears the singleton → such a file is re-emitted and re-executed on the next tick — identical to today's trigger-restart behavior ("downstream idempotency covers re-executions").
- `DilosFileConfirm@1` config: `serverConfiguration`, `deleteAfterSuccess`, `path` (default **`$.current`** — reads the ONE current file element; **never `$.files`**, which via parent fallback would be the whole array and would delete files whose iteration has not run yet). Runs as LAST child inside the ForEach: keep-mode → `MarkKeptOnServer(key)`; delete-mode → `sftp.DeleteFile(fullPath)` with pending-delete bookkeeping. Uses `ISftpFileSystemFactory` (DI singleton, `Program.cs:48`).

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] public async Task EmitsFiles_NameOrdered_RespectingMinFileAge_WithKeyAndFullPath()
[Fact] public async Task NoFiles_SeedsEmptyFilesArray()                         // $.files == []
[Fact] public async Task PendingDeleteRetry_DeletesDuringListing_WithoutEmitting() // parity with trigger :176-181
[Fact] public async Task KeepMode_SecondRun_SkipsAlreadyEmittedFiles()          // singleton carries keys
[Fact] public async Task ConfirmNode_ReadsSingleElementAtPath_NotWholeArray()   // $.current scope
[Fact] public async Task ConfirmNode_DeleteMode_DeletesFullPathAndClearsPending()
[Fact] public async Task ConfirmNode_KeepMode_MarksKeptOnServer()
```

- [ ] **Step 2:** FAIL → **Step 3:** implement all three → **Step 4:** suites green ×2 → **Step 5:** commit `feat(AB#4228): add DilosFileFetchStep@1 + DilosFileConfirm@1 with shared cross-tick state`.

### Task 4: Register the new nodes

**Files:** Modify `src/AdapterMeshWeClapp/Program.cs:55-61`

- [ ] **Step 1:** add `.RegisterNode<WeClappFetchStepNode>()`, `.RegisterNode<DilosFileFetchStepNode>()`, `.RegisterNode<DilosFileConfirmNode>()`; keep both trigger nodes registered (rollback compatibility until the standard-node switch). HttpClient decompression loop stays unchanged (Task 2 reuses the trigger client name).
- [ ] **Step 2:** suites green ×2; commit `feat(AB#4228): register step nodes`.

### Task 5: Contract-test groundwork + convert `as` (batch — zero downstream churn)

**Files:**
- Modify: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs`
- Modify: `pipelines/weclapp-articles-to-as.yaml`

- [ ] **Step 1: Serializer registrations (else the converted-file contract tests fail):** in `DeserializePipeline` (`PipelineYamlContractTests.cs:185-204`) add ONLY the three new adapter configs (`WeClappFetchStepNodeConfiguration`, `DilosFileFetchStepNodeConfiguration`, `DilosFileConfirmNodeConfiguration`). `FromPipelineTriggerEvent@1` needs NO extra registration here — the chain already calls `.AddMeshDataPipelineNodes()` (line 190), which registers it; `FromExecutePipelineCommand@1` and `ForEach@1` come from the base serializer (net10). (A duplicate registration would be harmless — duplicate-name guard — but is noise.)
- [ ] **Step 2: New guard test (RED):** there are NO helpers named `LoadPipelineYaml`/`GetTriggerTypes`/`RawText` — define them now: raw text via the class's existing `FindRepoFile(Path.Combine("pipelines", fileName))` + `File.ReadAllText` (bare file names throw `FileNotFoundException` — every existing caller passes the `pipelines/` prefix, `PipelineYamlContractTests.cs:201`); trigger types via `DeserializePipeline(fileName)` → `root.Triggers` config types mapped through their `[NodeName]`:

```csharp
[Theory]
[InlineData("weclapp-articles-to-as.yaml")]
public async Task ConvertedYaml_UsesPassiveTriggers_NoPollingFields(string file)
{
    var root = await DeserializePipeline(file);
    Assert.Collection(root.Triggers!,
        t => Assert.IsType<FromPipelineTriggerEventNodeConfiguration>(t),
        t => Assert.IsType<FromExecutePipelineCommandNodeConfiguration>(t));
    var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", file)));
    Assert.DoesNotContain("pollingIntervalSeconds", raw);
    Assert.DoesNotContain("runOnStart", raw);
}
```

- [ ] **Step 3: Convert the as YAML:** triggers → the two passive types; first transformation = `WeClappFetchStep@1` (baseUrl, apiKey `${WECLAPP_API_KEY}`, entity article, emitMode Batch, exportKind AS, pageSize 100). Downstream byte-identical. **Also reword ALL comments in this YAML that contain the literal words `pollingIntervalSeconds`/`runOnStart`** (header + K1/K2 blocks — the raw-text assertion scans comments too); replace the K2 note with "no start-tick by design (FromPipelineTriggerEvent)".
- [ ] **Step 4:** guard test GREEN for as; full suites ×2 green. **Step 5:** commit `feat(AB#4228): as pipeline on passive cron trigger`.

### Task 6: Convert `ck` + `ai` (ForEach fan-out)

**Files:**
- Modify: `pipelines/weclapp-articles-to-ck.yaml`, `pipelines/weclapp-orders-to-ai.yaml`
- Modify: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs`

- [ ] **Step 1:** add both files to the Task-5 theory (RED).
- [ ] **Step 2: Convert** using the **canonical ForEach block** (top of plan): fetch step first (ck → `$.articles`, ai → `$.orders`), then ForEach with children = existing chain, **segment-replaced**: `$.item…` → `$.current.item…`, `$.customer` → `$.current.customer` (uniform for ck and ai thanks to the `{item: …}` wrapping).
- [ ] **Step 3: Fix the path-resolution tests (per-test reality, source-checked):** `ArticlesToCkYaml_ConfiguredPaths_ResolveAgainstTransformOutput` (:72) is the only genuinely root-only test — give it descent by reusing the existing recursive `Walk` helper (`PipelineYamlContractTests.cs:206-239`, already traverses `IChildNodeConfiguration` incl. ForEach). `OrdersToAiYaml_CustomerNameUpdate_ResolvesForB2cCustomers` (:104) already descends into `gate.Transformations` — only its root-level anchors (toCk/gate lookup) move below the ForEach; find them via `Walk`. `OrdersToAiYaml_ConfiguredCkPaths_…` (:134) already uses `Walk` — NO structural change needed there. All three: lift the hardcoded fixture documents to the `$.current` shape (`{"current":{"item":…,"customer":…}}`).
- [ ] **Step 4:** suites ×2 green. **Step 5:** commit `feat(AB#4228): ck+ai pipelines on passive cron trigger with ForEach fan-out`.

### Task 7: Convert `ar` + `be` (file fan-out + confirm)

**Files:** `pipelines/dilos-ar-to-weclapp.yaml`, `pipelines/dilos-be-to-weclapp.yaml`; theory `InlineData` extension.

- [ ] **Step 1:** theory RED for both.
- [ ] **Step 2:** `DilosFileFetchStep@1` (serverConfiguration LkvSftp, remoteDirectory "/", filePattern as-is, minFileAgeSeconds 60, deleteAfterSuccess false) → canonical ForEach over `$.files`, children = existing `WeClappArWrite@1`/`WeClappBeWrite@1` with `fileNamePath: $.current.fileName`, `contentPath: $.current.content` (configurable paths exist, `WeClappWriteNodeConfiguration.cs:21-24`), final child = `DilosFileConfirm@1` (`path: $.current`). Comment sweep as in Task 5 Step 3.
- [ ] **Step 3:** suites ×2 green. **Step 4:** commit `feat(AB#4228): ar+be pipelines on passive cron trigger with per-file ForEach`.

### Task 8: Generator emits PipelineTrigger entities (opt-in)

**Files:** Modify `C:\Users\martin-lt\Development\LKV-Vorbereitung\scripts-prod\build-rt-weclapp.ps1`

- [ ] **Step 0: Backup** (folder is not a git repo): copy to `build-rt-weclapp.ps1.bak-2026-08-05`.
- [ ] **Step 1:** new switch **`-EmitPipelineTriggers` (default OFF)** — prod/test-2 runs must stay byte-compatible with the r3.4.74 pipelines; only staging runs for this work set the switch. New function `Add-PipelineTrigger($RtId, $Name, $PipelineRtId, $DataFlowRtId, $Cron, [bool]$Enabled)` emitting the entity exactly like the Task-1 probe block (ParentChild→DataFlow, Triggers→Pipeline, `System.Bot/CronExpression`); emit the five entities per the map, **inside the existing `$SkipReturnPath`/`$SkipOutbound` guards** (ar/be triggers only with the return path, as/ck/ai only with outbound — an unguarded emission would reference association targets missing from the document, and a PipelineTrigger without its Triggers association lands in DeploymentState Error); `Enabled` coupled to `-DisabledPipelines` like the pipelines. **Document in the script header: importing PipelineTrigger entities activates nothing — after the import run `octo-cli -c DeployTriggers`; an Enabled flip likewise bites only after the next DeployTriggers/tenant restart.**
- [ ] **Step 2: Smoke with fake data** (scratchpad; fake creds file + fake env): without the switch → 0 `PipelineTrigger` blocks (regression!); with it → 5 blocks, correct crons, `-DisabledPipelines ar` flips exactly ar's trigger. Delete scratch output.
- [ ] **Step 3:** no repo commit (script lives outside); record in the project chronicle.

### Task 9: Full verification pass

- [ ] Suites ×2 for both test projects (record counts) → `dotnet format --verify-no-changes` → `grep -rn "pollingIntervalSeconds\|runOnStart" pipelines/` = **0 hits including comments**.

### Task 10: Documentation

**Files:** `CLAUDE.md` + `readme.md` (the node catalog lives in `readme.md:9-14` — `docs/developer-guide.md` does NOT exist; add the 3 new nodes; mark both trigger nodes "legacy, superseded"); plus in `LKV-Vorbereitung`:
- `PROD-2-RUNBOOK-2026-08-03.md` S5: "Generate prod RT-YAML from a `r3.4.74` worktree: `git worktree add ..\weclapp-r3474 r3.4.74`, then run the generator with `-RepoDir <worktree>` and WITHOUT `-EmitPipelineTriggers` — main's pipelines require step nodes 3.4.74 does not contain." **Plus (applies from the step-node chart onward): after any RT import that carries PipelineTrigger entities, `octo-cli -c DeployTriggers` is mandatory — import alone schedules nothing.**
- `HAERTETEST-DREHBUCH-STAGING-2026-08-03.md`: add a clearly marked block **"gilt erst ab Chart mit Step-Nodes (> 3.4.74)"** with the new T3/T6 expectations (no start-tick on redeploy; keep-mode re-log after pod restart) — the existing T3/T6 wording for 3.4.74 runs stays untouched.
- `TRIGGER-TRENNUNG-DESIGN-2026-08-03.md`: status "implemented on branch" + evidence links.

- [ ] One step per file; commit repo files `docs(AB#4228): trigger separation notes`.

### Task 11: PR preparation ⛔ push/PR only with Martin's OK

- [ ] Draft EN PR body → `C:\Users\martin-lt\Development\LKV-Vorbereitung\PR-BESCHREIBUNG-TRIGGER-SEPARATION-EN.md` (what/why, conversion table, parity notes, test counts, "prod unaffected: pinned to r3.4.74") — no AI footer.
- [ ] STOP. Show Martin diff summary + PR body. Push + `gh pr create --reviewer mmgerald` ONLY after his OK.

## Follow-ups (separate plans/steps — NOT part of this plan)

0. **Post-merge staging verification (⛔ GO-gated, the real proof):** after merge + next 3.4.x train → lift staging chart to the step-node version → RT import **with `-EmitPipelineTriggers`** and the `-SftpUser` staging parameters → **`octo-cli -c DeployTriggers`** (import alone schedules nothing) → Härtetest T1–T7 on the rebuilt stand (per the agreed "one Härtetest, on the final state"). This plan's Tasks only prove build+tests+probe — the converted pipelines cannot run on staging before a chart ships them.
1. `GetSftpFile@1` contribution (octo-mesh-adapter).
2. `RenderDelimitedText@1` contribution (flat mirror of ImportFromCsv@1).
3. `MakeHttpRequest@1` paging/retry/timeout contribution.
4. Adapter switch to standard nodes (removes legacy trigger nodes).

## Review log

- v1 self-review at write time (types cross-checked in-session).
- v2 adversarial review 05.08. (3 independent source-checking reviewers): fixed — RT import format (`entities:`/runtime-model schema), adapter binding via Pipeline-`Executes` (DataFlow has no associations/Enabled), contract-test serializer registrations, ForEach `targetPath`/`mergePath` explicit (root-overwrite trap), Confirm scope `$.current` (delete-all hazard), FileKey/fullPath in file elements, HttpClient gzip name reuse, ck `{item:…}` element shape, path-test descent + fixtures, branch Step 0, cron stagger (be :25, ck 02:07), generator opt-in switch + backup, Härtetest wording split by chart version, Task-1 fallback warning, `-r` flag syntax, comment sweeps, 0-article case. Confirmed intact: AI-marker persistence in iteration contexts, business-reject semantics, rtId/cron map, test-2 untouched, no RT namespace collision with G4.
- v3 independent review 05.08. evening (5-agent ultracode `wf_39b56265`, read-only vs. octo-mesh-adapter / octo-communication-sdk / octo-communication-controller-services / octo-cli / octo-adapter-weclapp): **1 BLOCKER** (PipelineTrigger import schedules nothing — `DeployTriggers` steps added to Tasks 1/8/10 + Follow-up 0; `UpdateScheduleAsync` callers grep-verified twice). Majors: Task-3 delete-retry parity split; always-seed-`[]` rule; Task-5 registration shrink + `FindRepoFile` prefix; Task-6 per-test reality (`Walk` already descends); ForEach merge-order unordered (ConcurrentBag) + maxDop default 0 = ProcessorCount; `dotnet restore` precondition (assets pinned 3.4.73); Task 10 → `readme.md`. Confirmed: rtId/cron map vs generator params, probe YAML vs `rt-dataflow-scheduled-trigger.yaml` + CK model (SystemCommunicationCkModel in octo-communication-controller-services), all octo-cli commands incl. valueless `-r`, adapter-registration state filter (delivers only Pending/Deployed — an all-0 tenant starts nothing on workload deploy), RT import = plain data write (no deploy side effects), node-ctor DI pattern (`next` + resolved deps + optional `TimeProvider`), as-chain parity incl. 0-article no-marker proof (`DilosRenderNode.cs:58-66`), `Logger@1` requires `message` (base-registered), YAML binding camelCase + strict (typos fail at import).
