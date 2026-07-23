# CLAUDE.md - OctoMesh WeClapp Adapter

## Project Overview
Mesh adapter connecting the WeClapp ERP to OctoMesh for LKV Logistik: pulls
orders/articles from the WeClapp REST API into `Industry.Logistics` CK instances
and renders the DILOS file contract (AS/AI outbound; AR/BE return path parsed by
the core lib). Template: `octo-adapter-demos` (Mesh adapter / Socket).

## Build Commands
```bash
# Local development build (uses local NuGet packages from ../nuget/)
dotnet build Octo.WeClappAdapter.slnx -c DebugL

# Run tests
dotnet test Octo.WeClappAdapter.slnx -c DebugL
```

## Project Structure
- `src/AdapterMeshWeClapp/` - Mesh adapter host (cloud, connects directly to OctoMesh
  repositories) + all custom pipeline nodes (outbound: `WeClappFetch@1`, `WeClappToCk@1`,
  `DilosRender@1`, `DilosSftpWrite@1` [ISO-8859-1 delivery]; return path: `DilosFileFetch@1`,
  `WeClappArWrite@1`, `WeClappBeWrite@1`)
- `src/Lkv.WeClapp.Core/` - plain core lib: WeClapp DTOs/JSON, WeClapp→DILOS value rules,
  DILOS AS/AI writers, DILOS AR/BE parsers + write-back planners (fail-loud, golden-file verified)
- `src/charts/octo-weclapp-adapter/` - Helm chart (deployed by the Communication Operator;
  httpGet probes on `/healthz/live|ready`)
- `pipelines/` - tenant pipeline YAMLs (orders→AI per order; articles split into per-item
  CK sync + batched AS delivery [`emitMode: Batch`, one file per poll]; AR/BE return path);
  `scripts/om_setup_lkv.ps1` substitutes `${WECLAPP_API_KEY}` + `REPLACE-TENANT` baseUrl
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
- Redeploy determinism (P2): the AS batch pipeline runs delay-first (`runOnStart: false`) and
  gates delivery on a per-day CK marker (`Industry.Logistics/ExportRun`), so a (re)deploy emits
  no immediate or duplicate AS file; ck/ai keep `runOnStart: true` (ck idempotent, ai gated)

## Domain Gotchas (golden-file verified — do not "fix" without evidence)
- DILOS AR/BE use **comma** decimals; AI/AS use dot. Both verified against real files.
- AR tracking value can be a carrier URL with the single tracking number DUPLICATED
  after a comma (`p=X,X`) — a splitter must dedupe. Carrier codes outside the spec
  table exist (golden: `9`).
- `Gesamtmenge` (AR K* field 12) includes the empty-ArticleNumber shipping pseudo-item.
- BE field count is customer-specific (LKV spec/golden: 6; old Billbee variant: 7 with
  SKU) — parsers fail loud on mismatch by design.

## AR/BE Return Path (SFTP → WeClapp)
- `DilosFileFetch@1` polls the LKV SFTP (credentials via tenant GlobalConfiguration
  entry `LkvSftp`, same JSON shape as `SftpUpload@1`) and starts ONE pipeline execution
  per file `{fileName, content}`; with `deleteAfterSuccess: true` the remote file is
  deleted only AFTER the awaited execution succeeded → the downstream write MUST stay
  idempotent. The DEFAULT is the safe side (false = keep files): a dry-run execution
  succeeds without writing, deleting would consume the LKV file with no effect — flip
  `deleteAfterSuccess: true` together with `dryRun: false` for go-live. Importing a
  pipeline YAML that uses a config key the DEPLOYED image does not know yet fails the
  pipeline registration (the SDK YAML deserializer rejects unknown properties) → deploy
  the new image before importing updated YAMLs.
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
  are skipped and logged. Trial writes use `WECLAPP_TRIAL_*` env vars ONLY —
  **`WECLAPP_CUSTOMER_*` is a productive system: GET only, never writes.**
- Live smokes are multi-gated: real writes additionally require `WECLAPP_TRIAL_REAL_WRITE=1`
  (process-scoped for ONE deliberate run — never persist it) and the SFTP E2E also needs
  `LKV_SFTP_CREDENTIALS_FILE`; a normal `dotnet test` stays a no-op.

## Conventions
- `TreatWarningsAsErrors`, nullable enabled, `LangVersion latestmajor` (Directory.Build.props)
- Configurations: `Debug`, `Release`, `DebugL` (local NuGet at `../nuget/`, version `999.0.0`)
- English code + XML docs; DILOS original field names + 1-based field index in XML docs
- Commit messages: `AB#4228: <meaningful description>`

## Pre-Commit Checklist (ALL steps MUST pass)
1. `dotnet format Octo.WeClappAdapter.slnx --verify-no-changes`
2. `dotnet build Octo.WeClappAdapter.slnx -c DebugL`
3. `dotnet test Octo.WeClappAdapter.slnx -c DebugL`
