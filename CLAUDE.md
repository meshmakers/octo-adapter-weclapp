# CLAUDE.md - OctoMesh WeClapp Adapter

## Project Overview
Mesh adapter connecting the WeClapp ERP to OctoMesh for LKV Logistik: pulls
orders/articles from the WeClapp REST API into `Industry.Logistics` CK instances
and renders the DILOS file contract (AS/AI outbound; AR/BE return path parsed by
the core lib). Template: `octo-adapter-demos` (Mesh adapter / Socket).

## Build Commands
```bash
# Local development build (uses local NuGet packages from ../nuget/)
dotnet build Octo.AdapterWeClapp.slnx -c DebugL

# Run tests
dotnet test Octo.AdapterWeClapp.slnx -c DebugL
```

## Project Structure
- `src/AdapterMeshWeClapp/` - Mesh adapter host (cloud, connects directly to OctoMesh repositories)
- `src/Lkv.WeClapp.Core/` - plain core lib: WeClapp DTOs/JSON, WeClapp→DILOS value rules,
  DILOS AS/AI writers, DILOS AR/BE parsers (fail-loud, golden-file verified)
- `tests/Lkv.WeClapp.Core.Tests/` - xUnit against real LKV golden fixtures
- `docs/superpowers/` - design specs and implementation plans

## Key Patterns
- Mesh adapter: `WebAdapterBuilder` (passive/Socket), SDK `Microsoft.NET.Sdk`
- `IAdapterService` for startup/shutdown lifecycle (pipeline registration + event hub)
- Pipeline nodes implement `IPipelineNode`, trigger nodes `ITriggerPipelineNode`;
  configuration via `[NodeName]` and `[NodeConfiguration]` attributes
- Custom nodes live in THIS repo (official adapter guideline), not in octo-mesh-adapter
- Primary constructors with DI (C# 12+)

## Domain Gotchas (golden-file verified — do not "fix" without evidence)
- DILOS AR/BE use **comma** decimals; AI/AS use dot. Both verified against real files.
- AR tracking value can be a carrier URL with the single tracking number DUPLICATED
  after a comma (`p=X,X`) — a splitter must dedupe. Carrier codes outside the spec
  table exist (golden: `9`).
- `Gesamtmenge` (AR K* field 12) includes the empty-ArticleNumber shipping pseudo-item.
- BE field count is customer-specific (LKV spec/golden: 6; old Billbee variant: 7 with
  SKU) — parsers fail loud on mismatch by design.

## Conventions
- `TreatWarningsAsErrors`, nullable enabled, `LangVersion latestmajor` (Directory.Build.props)
- Configurations: `Debug`, `Release`, `DebugL` (local NuGet at `../nuget/`, version `999.0.0`)
- English code + XML docs; DILOS original field names + 1-based field index in XML docs
- Commit messages: `AB#4228: <meaningful description>`

## Pre-Commit Checklist (ALL steps MUST pass)
1. `dotnet format Octo.AdapterWeClapp.slnx --verify-no-changes`
2. `dotnet build Octo.AdapterWeClapp.slnx -c DebugL`
3. `dotnet test Octo.AdapterWeClapp.slnx -c DebugL`
