# octo-adapter-weclapp

OctoMesh **mesh adapter** for the WeClapp ERP ↔ LKV Logistik (DILOS) integration.
Structure per the [octo-adapter-demos](https://github.com/meshmakers/octo-adapter-demos)
template.

## Projects & layout

- `src/AdapterMeshWeClapp` — the adapter host (`WebAdapterBuilder`, `IAdapterService`,
  observability/health endpoints, pipeline registration) plus the custom pipeline nodes:
  - outbound: `WeClappFetch@1` (trigger, per-item or batch), `WeClappToCk@1`,
    `DilosRender@1` (content + golden file names), `DilosSftpWrite@1` (ISO-8859-1 delivery)
  - return path: `DilosFileFetch@1` (SFTP trigger), `WeClappArWrite@1`, `WeClappBeWrite@1`
- `src/Lkv.WeClapp.Core` — plain .NET core library, no platform dependencies:
  - **WeClapp → DILOS (outbound)**: `WeClappJson`, `WeClappToDilos` value rules,
    `DilosArticleWriter` (AS `A*`), `DilosOrderWriter` (AI `K*`/`P*`)
  - **DILOS → WeClapp (return path)**: `DilosArParser` (AR `K*`/`C*`/`P*`/`L*` →
    `DilosArShipment` aggregates), `DilosBeParser` (BE stock lines), write-back
    planners (AR shipment / BE stock delta)
- `src/charts/octo-weclapp-adapter` — Helm chart; deployed by the Communication
  Operator, probes `/healthz/live` + `/healthz/ready`
- `pipelines/` — tenant pipeline YAMLs (3× outbound: orders→AI per order,
  articles→CK per item, articles→AS as at most one batched file per Vienna calendar day; 2× return path);
  `scripts/om_setup_lkv.ps1` prepares them (substitutes `${WECLAPP_API_KEY}` and
  the `REPLACE-TENANT` baseUrl)
  - **Redeploy determinism (P2):** the AS pipeline delays first (`runOnStart: false`) and
    gates delivery on a per-day CK marker (`Industry.Logistics/ExportRun`), so a (re)deploy
    emits no immediate or duplicate AS file; `ck`/`ai` keep `runOnStart: true`
    (ck idempotent, ai already gated). Operational constraint: keep the chart's
    `replicaCount: 1` — the gate's probe-to-persist window is race-free only with a
    single replica (two replicas could both deliver before the day marker lands)
- `tests/Lkv.WeClapp.Core.Tests` — xUnit against real LKV golden files
  (specs verified field-by-field; see `docs/superpowers/specs/`)
- `tests/AdapterMeshWeClapp.Tests` — node/pipeline tests plus multi-gated live smokes
  (no-ops in a normal `dotnet test`; real writes need explicit opt-in env vars)

## CI & deployment

`azure-pipelines.yml` builds the multi-arch Docker image `meshmakers/octo-weclapp-adapter`
and publishes the Helm chart `octo-weclapp-adapter` to the dev channel on every `main`
build (release channel on `r*` tags).

## Build & test

```powershell
# Local development build (uses local NuGet packages from ../nuget/)
dotnet build Octo.WeClappAdapter.slnx -c DebugL
dotnet test Octo.WeClappAdapter.slnx -c DebugL

# pre-commit gate
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes; dotnet build Octo.WeClappAdapter.slnx -c DebugL; dotnet test Octo.WeClappAdapter.slnx -c DebugL
```

Commits: `AB#4228: <description>` (Azure Boards work item link).
