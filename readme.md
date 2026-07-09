# octo-adapter-weclapp

OctoMesh **mesh adapter** for the WeClapp ERP ↔ LKV Logistik (DILOS) integration.
Structure per the [octo-adapter-demos](https://github.com/meshmakers/octo-adapter-demos)
template. Working name — final repo name TBD (octo-plug-weclapp vs. octo-adapter-weclapp).

## Projects

- `src/AdapterMeshWeClapp` — the adapter host (`WebAdapterBuilder`, `IAdapterService`,
  pipeline registration). Custom pipeline nodes (`WeClappFetch`, `WeClappToCk`,
  `DilosRender` — see the ingestion design) are added here as they are implemented.
- `src/Lkv.WeClapp.Core` — plain .NET core library, no platform dependencies:
  - **WeClapp → DILOS (outbound)**: `WeClappJson`, `WeClappToDilos` value rules,
    `DilosArticleWriter` (AS `A*`), `DilosOrderWriter` (AI `K*`/`P*`)
  - **DILOS → WeClapp (return path)**: `DilosArParser` (AR `K*`/`C*`/`P*`/`L*` →
    `DilosArShipment` aggregates), `DilosBeParser` (BE stock lines)
- `tests/Lkv.WeClapp.Core.Tests` — 57 xUnit tests against real LKV golden files
  (specs verified field-by-field; see `docs/superpowers/specs/`).

## Build & test

```powershell
# Local development build (uses local NuGet packages from ../nuget/)
dotnet build Octo.WeClappAdapter.slnx -c DebugL
dotnet test Octo.WeClappAdapter.slnx -c DebugL

# pre-commit gate
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes; dotnet build Octo.WeClappAdapter.slnx -c DebugL; dotnet test Octo.WeClappAdapter.slnx -c DebugL
```

Commits: `AB#4228: <description>` (Azure Boards work item link).
