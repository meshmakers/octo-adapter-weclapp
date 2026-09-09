# octo-adapter-weclapp

OctoMesh **mesh adapter** for the WeClapp ERP ↔ LKV Logistik (DILOS) integration.
Structure per the [octo-adapter-demos](https://github.com/meshmakers/octo-adapter-demos)
template.

## Projects & layout

- `src/AdapterMeshWeClapp` — the adapter host (`WebAdapterBuilder`, `IAdapterService`,
  observability/health endpoints, pipeline registration) plus the custom pipeline nodes:
  - outbound: `WeClappResolveSupplySources@1` (replaces the article supply-source stubs with the fetched
    entities that carry the EK prices, drops system articles and projects the DILOS EK-Preis),
    `DilosRender@1` (AI content + golden file name, and the join that gives every
    AI position its MwSt rate: a WeClapp position names a `taxId` but states no percentage, so the
    node reads the separately fetched `tax` entities named by its `taxesPath` — the same
    fetch-the-second-entity shape `WeClappResolveSupplySources@1` uses for the EK prices; the AS
    article master renders through the product's `RenderDelimitedText@1` with the 34 columns
    spelled out in the yaml, and the CK-shaped view of articles and orders is built by standard
    nodes in the ck/ai yamls; the fetching is the product's `MakeHttpRequest@1` and the delivery its
    `SftpUpload@1` with `encoding: iso-8859-1`)
  - return path: `WeClappArWrite@1`, `WeClappBeWrite@1` (the listing, the reading and the deleting
    are the product's `SftpList@1`, `SftpDownload@1` and `SftpDelete@1`; the per-file state is an
    `Industry.Logistics/InboundFile` marker written by standard nodes in the ar/be yamls)
- `src/Lkv.WeClapp.Core` — plain .NET core library, no platform dependencies:
  - **WeClapp → DILOS (outbound)**: `WeClappJson`, `WeClappToDilos` value rules,
    `DilosOrderWriter` (AI `K*`/`P*`; the AS `A*` layout is the column list in
    `pipelines/weclapp-articles-to-as.yaml`)
  - **DILOS → WeClapp (return path)**: `DilosArParser` (AR `K*`/`C*`/`P*`/`L*` →
    `DilosArShipment` aggregates), `DilosBeParser` (BE stock lines), write-back
    planners (AR shipment / BE stock delta)
- `src/charts/octo-weclapp-adapter` — Helm chart; deployed by the Communication
  Operator, probes `/healthz/live` + `/healthz/ready`
- `pipelines/` — tenant pipeline YAMLs (3× outbound: orders→AI per order,
  articles→CK per item, articles→AS as at most one batched file per Vienna calendar day; 2× return path).
  The YAMLs carry no credentials: WeClapp access comes from the tenant GlobalConfiguration
  entry `WeClappApi` (`apiConfiguration`), SFTP access from the entry `LkvSftp`. Three
  values remain tenant-specific and are marked REPLACE/TBD in the YAMLs — the AI
  submandant, the BE warehouseId and the AR/BE SFTP `remoteDirectory` (review before
  deploying to a new tenant); `scripts/om_setup_lkv.ps1` bootstraps the tenant
  - **Trigger architecture:** every pipeline carries two passive triggers —
    `FromPipelineTriggerEvent@1` (cron, subscribes a per-pipeline queue) and
    `FromExecutePipelineCommand@1` (manual/API run). A fetch step
    (`MakeHttpRequest@1` outbound, `SftpList@1` on the return path) runs
    first and seeds the data context; in
    4 of the 5 pipelines a per-item `ForEach@1` (`keyPath: $.current`,
    `maxDegreeOfParallelism: 1`) then fans the former per-execution chain out over the
    seeded array. TWO of the outbound pipelines make two fetches, for the same reason and in
    the same shape: the AS pipeline fetches `/article` plus the `/articleSupplySource`
    entities that carry the EK prices (both inside its per-day K1 gate), and the AI pipeline
    fetches `/salesOrder` plus the `/tax` entities that carry the VAT rates — the second fetch
    ahead of the per-order loop, so the rate set is read once per tick and not once per order.
    The AS pipeline is the one with no `ForEach@1`: it renders one batch per tick. Neither trigger polls or fires on
    (re)deploy — importing a `PipelineTrigger` RT entity schedules nothing by itself; only
    `octo-cli -c DeployTriggers` activates the schedule (also required again after any
    `Enabled` flip)
  - **Redeploy determinism (P2 — superseded by the trigger architecture above):** the former
    `runOnStart`/`pollingIntervalSeconds` fields no longer exist on any pipeline; nothing fires
    on (re)deploy or pod restart by construction. The AS pipeline still gates delivery on a
    per-day CK marker (`Industry.Logistics/ExportRun`, at most one file per Vienna calendar
    day). Operational constraint unchanged: keep the chart's `replicaCount: 1` — the gate's
    probe-to-persist window is race-free only with a single replica (two replicas could both
    deliver before the day marker lands)
- `tests/Lkv.WeClapp.Core.Tests` — xUnit against real LKV golden files
  (specs verified field-by-field; see `docs/superpowers/specs/`)
- `tests/AdapterMeshWeClapp.Tests` — node/pipeline tests plus multi-gated live smokes
  (no-ops in a normal `dotnet test`; real writes need explicit opt-in env vars)

## CI & deployment

`azure-pipelines.yml` builds the multi-arch Docker image `meshmakers/octo-weclapp-adapter`
and publishes the Helm chart `octo-weclapp-adapter` to the dev channel on every `main`
build (release channel on `r*` tags).

**Deploy the image before importing changed YAMLs - except when a yaml stops using an adapter
node.** A pipeline definition is deserialized strictly, so a yaml naming a config key the running
image does not know fails its registration at the tenant — and the reverse order fails later and
quieter, at RUN time, once a node is asked for behaviour the stored definition no longer describes.
`weclapp-orders-to-ai.yaml` carries the new `taxesPath` on `DilosRender@1`, so that release was
incompatible in both directions: roll the image out first, then re-import the yaml. The CK-view
swap (AB#5162) is the inverse case: the image drops `WeClappToCk@1`, a stored ck or ai definition
that still names it is rejected at load time, and the yamls use nothing an image on SDK 3.4.109 or
later lacks - so import `weclapp-articles-to-ck.yaml` and `weclapp-orders-to-ai.yaml` FIRST, then
roll the image. The file-state swap (`DilosFileGate@1`/`DilosFileConfirm@1` replaced by
`Industry.Logistics/InboundFile` markers and `SftpDelete@1`) is the same inversion: import CK 2.1.0,
then the ar/be yamls, then the image.

## Build & test

```powershell
# Build and test against the published SDK (nuget.org, 3.4.*)
dotnet build Octo.WeClappAdapter.slnx -c Debug
dotnet test Octo.WeClappAdapter.slnx -c Debug

# pre-commit gate
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes; dotnet build Octo.WeClappAdapter.slnx -c Debug; dotnet test Octo.WeClappAdapter.slnx -c Debug

# only while co-developing unreleased SDK changes (local ../nuget/ feed, 999.0.0)
dotnet build Octo.WeClappAdapter.slnx -c DebugL
```

Commits: Conventional Commits scoped to the work item — `<type>(AB#4228): <description>` (e.g.
`feat(AB#4228): …`, `fix(AB#4228): …`; the `AB#4228` scope links the Azure Boards work item).
