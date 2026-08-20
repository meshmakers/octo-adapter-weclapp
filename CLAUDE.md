# CLAUDE.md - OctoMesh WeClapp Adapter

## Project Overview
Mesh adapter connecting the WeClapp ERP to OctoMesh for LKV Logistik: pulls
orders/articles from the WeClapp REST API into `Industry.Logistics` CK instances
and renders the DILOS file contract (AS/AI outbound; AR/BE return path parsed by
the core lib). Template: `octo-adapter-demos` (Mesh adapter / Socket).

## Build Commands
```bash
# Build & test against the published SDK (nuget.org, 3.4.* - the line the deployed image carries)
dotnet build Octo.WeClappAdapter.slnx -c Debug
dotnet test Octo.WeClappAdapter.slnx -c Debug

# Only while co-developing unreleased SDK changes: 999.0.0 from the hand-maintained ../nuget/
dotnet build Octo.WeClappAdapter.slnx -c DebugL
```

## Project Structure
- `src/AdapterMeshWeClapp/` - Mesh adapter host (cloud, connects directly to OctoMesh
  repositories) + all custom pipeline nodes (outbound: `WeClappFetchStep@1`, `WeClappToCk@1`,
  `DilosRender@1`, `DilosSftpWrite@1` [ISO-8859-1 delivery]; return path: `DilosFileFetchStep@1`,
  `DilosFileConfirm@1`, `WeClappArWrite@1`, `WeClappBeWrite@1`; legacy poll-trigger nodes
  `WeClappFetch@1`/`DilosFileFetch@1` stay registered for rollback, see "Pipeline Trigger
  Architecture" below)
- `src/Lkv.WeClapp.Core/` - plain core lib: WeClapp DTOs/JSON, WeClapp→DILOS value rules,
  DILOS AS/AI writers, DILOS AR/BE parsers + write-back planners (fail-loud, golden-file verified)
- `src/charts/octo-weclapp-adapter/` - Helm chart (deployed by the Communication Operator;
  httpGet probes on `/healthz/live|ready`)
- `pipelines/` - tenant pipeline YAMLs (orders→AI per order; articles split into per-item
  CK sync + batched AS delivery [`emitMode: Batch`, at most one file per Vienna calendar day —
  K1 gate]; AR/BE return path); the YAMLs carry no credentials — WeClapp access comes
  from the tenant GlobalConfiguration entry `WeClappApi` (`apiConfiguration`), SFTP from
  `LkvSftp`; still tenant-specific and marked REPLACE/TBD in the YAMLs: AI submandant,
  BE warehouseId, AR/BE `remoteDirectory`; `scripts/om_setup_lkv.ps1` bootstraps the tenant and
  `scripts/_general/rt-adapter-weclapp.yaml` carries the `System.Communication/Adapter` RT entity
  (well-known name `WeClappAdapter`) — the `PipelineTrigger` entities live outside this repo
- `tests/Lkv.WeClapp.Core.Tests/` - xUnit against real LKV golden fixtures
- `tests/AdapterMeshWeClapp.Tests/` - node/pipeline tests + env-gated live smokes (gates below)
- `docs/superpowers/` - design specs and implementation plans

## Key Patterns
- Mesh adapter: `WebAdapterBuilder` (passive/Socket), SDK `Microsoft.NET.Sdk`
- `IAdapterService` for startup/shutdown lifecycle (pipeline registration + event hub)
- Pipeline nodes implement `IPipelineNode`, trigger nodes `ITriggerPipelineNode`;
  configuration via `[NodeName]` and `[NodeConfiguration]` attributes
- Custom nodes live in THIS repo (official adapter guideline), not in octo-mesh-adapter
- Primary constructors with DI (C# 12+)
- Observability is mandatory: `builder.AddObservability().AddSystemContextHealthCheck()` +
  `app.MapObservability()` — the chart's probes hit `/healthz/live|ready`; without the
  mapping the pod never becomes ready
- Node logs are message templates with args (`nodeContext.Info("... {0}", x)`) — NEVER
  interpolated strings: `INodeContext` forwards to structured logging, so a literal `{...}`
  (JSON body, URL) corrupts the template
- Redeploy determinism (P2 — superseded by "Pipeline Trigger Architecture" below): no pipeline
  has `runOnStart`/`pollingIntervalSeconds` anymore; nothing fires on (re)deploy or pod restart
  by construction. AS still gates delivery on a per-day CK marker
  (`Industry.Logistics/ExportRun`, at most one file per Vienna calendar day)

## Pipeline Trigger Architecture
All 5 pipeline YAMLs carry two passive triggers — `FromPipelineTriggerEvent@1` (cron,
subscribes a per-pipeline queue and calls `ExecuteAsync` directly) and
`FromExecutePipelineCommand@1` (manual/API run, e.g. for Härtetest probing). Neither is a poll
loop: a redeploy or pod restart fires no execution. A fetch step
(`WeClappFetchStep@1`/`DilosFileFetchStep@1`) runs first and seeds the data context at a fixed
root path — always the array, even `[]` (a missing/non-array path aborts a downstream
`ForEach@1` with `PathMustBeArray`); in 4 of the 5 YAMLs a per-item `ForEach@1` then fans the
former per-execution chain out over that array, one iteration per element
(`weclapp-articles-to-as.yaml` has no `ForEach@1` — it runs one Batch execution per tick).

**Canonical ForEach block** (use exactly this shape — the guard tests below pin
`keyPath`/`targetPath`/`maxDegreeOfParallelism` against every shipped `ForEach@1`):
```yaml
  - type: ForEach@1
    iterationPath: $.orders          # or $.articles / $.files
    keyPath: $.current
    # no mergePath: the default ($.key) merges nothing as long as no child writes $.key —
    # and merging $.current would deep-clone every item's full content into the result array
    targetPath: $.loopResult         # NEVER omit: default "$" REPLACES the document root
    maxDegreeOfParallelism: 1        # NEVER omit: default 0 = Environment.ProcessorCount (parallel!)
    transformations:
      # former chain, item segment replaced: $.item → $.current.item etc.
```
Merge results keep source order since AB#4760, but the contract is unchanged: **nothing may
read `$.loopResult`**; every child writes through the data context instead
(`ApplyChanges@1/@2`, `DilosSftpWrite@1`, `WeClappArWrite@1`, ...).

**Activation:** importing a `System.Communication/PipelineTrigger` RT entity schedules NOTHING
by itself — schedules materialize only via `octo-cli -c DeployTriggers` (or tenant start). Run
it after every RT import that carries `PipelineTrigger` entities; an `Enabled` flip via
re-import likewise only bites at the next `DeployTriggers`/tenant restart.

**Guard tests** (`tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs`) pin this against
the shipped YAMLs: `ConvertedYaml_UsesPassiveTriggers_NoPollingFields` asserts both passive
triggers in order, the correct first fetch-step type per file, and that
`pollingIntervalSeconds`/`runOnStart` appear nowhere in the raw text (including comments);
`ConvertedYaml_UsesPassiveTriggers_TheoryCoversAllPipelineYamls` keeps that Theory's
`[InlineData]` rows in lockstep with the pipelines actually shipped, so a future 6th yaml cannot
silently escape the ban; `AllPipelineYamls_EveryForEach_HasNonRootTargetPathAndSequentialDop`
asserts every `ForEach@1` has a non-null, non-`"$"` `targetPath` and
`maxDegreeOfParallelism == 1`; `AllPipelineYamls_EveryForEach_KeyPathIsCurrent` pins every
`ForEach@1`'s `keyPath` to `$.current`; `AllPipelineYamls_DilosFileFetchStepAndConfirm_DeleteAfterSuccessMatches`
asserts `DilosFileFetchStep@1`/`DilosFileConfirm@1` carry the same `deleteAfterSuccess` AND
`serverConfiguration` in every ar/be yaml;
`ArBeYamls_DilosFileConfirm_IsTheLastPerFileForEachChild` pins the confirm node as the LAST
per-file `ForEach@1` child; `ArBeYamls_DryRunWriteNode_ForbidsDeleteAfterSuccess` forbids
`deleteAfterSuccess: true` while the write node runs `dryRun: true`;
`AllPipelineYamls_UseApiConfigurationOnly_NoInlineCredentialsOrPlaceholders` keeps WeClapp access
on `apiConfiguration` (no inline `apiKey`/`baseUrl`, no substitution placeholder);
`AllPipelineYamls_EveryAttributeUpdate_DeclaresValueType` requires every `ApplyChanges` attribute
update to declare its `valueType`; `ArticlesToCkYaml_ConfiguredPaths_ResolveAgainstTransformOutput`
and `OrdersToAiYaml_ConfiguredCkPaths_ResolveAgainstOrderTransformOutput` resolve every configured
value path against the REAL `WeClappToCk@1` output (Article mode writes `$.ck` FLAT, Order mode
NESTED), so a path that silently resolves to null cannot ship;
`OrdersToAiYaml_CustomerNameUpdate_ResolvesForB2cCustomers` covers the B2C case below.

This list is machine-checked: `DocumentationContractTests.ClaudeMd_NamesEveryPipelineContractTest`
fails the suite if a `PipelineYamlContractTests` guard is not named here. It drifted once (AB#4845
added two guards without documenting them, leaving 5 of 12 listed) — an inventory that claims to
be complete and is not invites re-pinning an invariant that already holds.

## Domain Gotchas (golden-file verified — do not "fix" without evidence)
- DILOS AR/BE use **comma** decimals; AI/AS use dot. Both verified against real files.
- AR tracking value can be a carrier URL with the single tracking number DUPLICATED
  after a comma (`p=X,X`) — a splitter must dedupe. Carrier codes outside the spec
  table exist (golden: `9`).
- `Gesamtmenge` (AR K* field 12) includes the empty-ArticleNumber shipping pseudo-item.
- BE field count is customer-specific (LKV spec/golden: 6; old Billbee variant: 7 with
  SKU) — parsers fail loud on mismatch by design.
- B2C orders carry an EMPTY WeClapp `customer.company` (the person is in `firstName`/`lastName`).
  TWO independent fallbacks share the shape "company, else `FirstName LastName`" but NOT the
  source: `WeClappToCkNode` builds `CkCustomer.Name` from the CUSTOMER record — the orders→AI yaml
  must write that value (`valuePath: $.ck.Customer.Name`), because a path aimed at the raw company
  field leaves the CK name empty for B2C (live finding 2026-07-16). `DilosOrderWriter`
  (`RecipientName1`/`RecipientName2`) builds the DILOS FILE name fields from the ADDRESS instead;
  name2 ("Nachname Vorname") stays empty unless a company fills name1.

## AR/BE Return Path (SFTP → WeClapp)
- `DilosFileFetchStep@1` lists the LKV SFTP (credentials via tenant GlobalConfiguration
  entry `LkvSftp`, same JSON shape as `SftpUpload@1`) once per cron tick and seeds `$.files`
  with every matching, ready file (always the array, even `[]`); a per-file `ForEach@1`
  (`keyPath: $.current`) then fans out the write chain — `WeClappArWrite@1`/`WeClappBeWrite@1`
  followed by `DilosFileConfirm@1` as the LAST child. `DilosFileConfirm@1`, not the fetch step,
  performs the actual keep/delete: with `deleteAfterSuccess: true` it deletes the remote file
  only AFTER the write succeeded → the write MUST stay idempotent. The DEFAULT is the safe side
  (false = keep files): a dry-run execution succeeds without writing, deleting would consume the
  LKV file with no effect — flip `deleteAfterSuccess: true` together with `dryRun: false` for
  go-live (`DilosFileFetchStep@1` and `DilosFileConfirm@1` must carry the SAME value). Importing
  a pipeline YAML that uses a config key the DEPLOYED image does not know yet fails the pipeline
  registration (the SDK YAML deserializer rejects unknown properties) → deploy the new image
  before importing updated YAMLs.
- `WeClappArWrite@1`: AR K* Auftragsnummer1 = WeClapp `salesOrder.id` (404 = dead-letter
  log, file still consumed). Idempotency: SHIPPED shipment with same tracking = skip;
  reuse non-CANCELLED; else `createShipment`. Quantities match by **articleId, never by
  position**; the data PUT echoes the COMPLETE shipmentItems list; `{"status":"SHIPPED"}`
  is a separate LAST PUT. `warehouseStock`/`salesOrder.shipped` are read-only — never
  write them for AR.
- `WeClappBeWrite@1`: BE is an absolute snapshot → delta bookings via
  `bookIncomingMovement`/`bookOutgoingMovement` (warehouseStock is GET-only). GES lines
  and unknown articles are skipped loudly. BE lines are aggregated per resolved WeClapp
  articleId BEFORE delta planning — variant lines (Characteristic1/2) can collapse to one
  article and would otherwise double-book.
- `DryRun` on both write nodes: PUTs run with `?dryRun=true`; createShipment/movements
  are skipped and logged.
- Live smoke: `WeClappCustomerSmokeTests` is env-gated on `WECLAPP_CUSTOMER_*` and
  **strictly read-only** — the customer system is productive: **GET only, never writes.**
  The former trial-account smokes (dry-run writes, real writes, SFTP E2E) died with the
  trial account (expired 2026-07) and were removed — do not revive write smokes against
  the customer system. A normal `dotnet test` without the env vars stays a no-op.

## Conventions
- `TreatWarningsAsErrors`, nullable enabled, `LangVersion latestmajor` (Directory.Build.props)
- Configurations: `Debug`/`Release` restore the published SDK from nuget.org (`3.4.*`); `DebugL`
  restores `999.0.0` from the hand-maintained `../nuget/` folder. That folder is filled by hand
  and CAN lag behind: a `PipelineSerializationException` like `Property 'continueOnError' not
  found on ForEachNodeConfiguration` means the feed is older than the SDK feature the pipelines
  use — not a code defect. Refresh order on a feed break: `octo-sdk` → `octo-communication-sdk`
  → consumer. `DebugL` also defines `DEBUGL`, NOT `DEBUG` (MSBuild derives the constant from the
  configuration name), so `#if DEBUG` blocks and `[Conditional("DEBUG")]` calls such as
  `Debug.Assert` compile away there — the repo currently uses none.
- English code + XML docs; DILOS original field names + 1-based field index in XML docs
- Commit messages: Conventional Commits scoped to the work item —
  `<type>(AB#4228): <meaningful description>` (types used in this repo: `feat`, `fix`, `test`,
  `docs`, `style`, `refactor`)

## Pre-Commit Checklist (ALL steps MUST pass)
1. `dotnet format Octo.WeClappAdapter.slnx --verify-no-changes`
2. `dotnet build Octo.WeClappAdapter.slnx -c Debug`
3. `dotnet test Octo.WeClappAdapter.slnx -c Debug`

`Debug` is the gate: it tests against the SDK the deployed image actually carries. Add
`-c DebugL` on top only while co-developing unreleased SDK changes.
