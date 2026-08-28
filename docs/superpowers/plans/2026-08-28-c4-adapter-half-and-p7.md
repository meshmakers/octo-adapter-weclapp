# C4 adapter half + P7 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the DILOS AS delivery onto the product node `RenderDelimitedText@1`, source the AS
file name from the same Vienna clock read as the export-day marker, and delete the four legacy
fetch node types no shipped pipeline uses any more.

**Architecture:** Three independent slices, landed in this order. P7 (AB#4843) is a pure deletion
with two test migrations. Then the AS file name moves from `DilosRender@1.fileNameTargetPath` to
`DilosExportRunKey@1` (one clock read, D3). Then the AS branch of `DilosRender@1` is replaced by a
composition of an adapter preparation step (`WeClappResolveSupplySources@1`, which gains the EK
price projection and the system-article filter) and the product's `RenderDelimitedText@1` with all
34 DILOS columns spelled out in the yaml, guarded by an `If@1` empty gate.

**Tech Stack:** .NET 10, xUnit, FakeItEasy, `Meshmakers.Octo.Sdk.MeshAdapter` 3.4.101 /
`Meshmakers.Octo.MeshAdapter.Nodes` 3.4.101 (resolved from nuget.org through the floating `3.4.*`
in `Directory.Build.props`).

**Spec:** `C:\Users\martin-lt\Development\LKV-Vorbereitung\RENDER-DELIMITED-TEXT-SPEC-DRAFT-2026-08-27.md`
(decisions D1-D7: `RENDER-DELIMITED-TEXT-ENTSCHEIDUNGSVORLAGE-2026-08-27.md`; session brief:
`C4-ADAPTERHAELFTE-STARTER-2026-08-28.md`).

## Verified ground truth (checked in this session, 2026-08-28, against code and packages)

Everything below was read off the repository or the restored packages, not off the documents.

- Baseline before the first change: **373/373 green in Debug AND Release**
  (`Lkv.WeClapp.Core.Tests` 130 + `AdapterMeshWeClapp.Tests` 243), 0 failed, 0 skipped.
- `dotnet restore` without `--force` held a stale **3.4.95** resolution. `--force` resolves
  `Meshmakers.Octo.Sdk.MeshAdapter 3.4.101` and `Meshmakers.Octo.MeshAdapter.Nodes 3.4.101`.
  `RenderDelimitedText` is present in `Meshmakers.Octo.Sdk.MeshAdapter.dll`; the configuration type
  `Meshmakers.Octo.MeshAdapter.Nodes.Transform.RenderDelimitedTextNodeConfiguration` is documented
  in `Meshmakers.Octo.MeshAdapter.Nodes.xml` with `Delimiter`, `LineEnding`, `TrailingNewLine`,
  `OnDelimiterInValue`, `Replacement`, `Columns` and the resolved-at-read-site defaults
  `DefaultLineEnding` / `DefaultTrailingNewLine` / `DefaultOnDelimiterInValue`.
  `DelimitedColumn` carries `Value`, `ValuePath`, `Required`; `Required` fails on an EMPTY rendered
  value, not merely on an absent one.
- No yaml under `pipelines/` references `WeClappFetch@1`, `WeClappFetchStep@1`, `DilosFileFetch@1`
  or `DilosFileFetchStep@1` (re-grepped here). `Program.cs` registers 12 custom types today; the
  four deletions leave **8**.
- **The session brief is wrong on one load-bearing point.** It says the EK price rule "is already
  done before the render (`WeClappResolveSupplySources@1`)". It is not:
  `WeClappResolveSupplySourcesNode` only replaces the `supplySources` reference stubs with the full
  `articleSupplySource` entities. Column 20 is still computed inside
  `DilosArticleWriter.RenderLine` as `Num(EkPreis(a.PurchasePrice))` - first parseable
  `supplySources[*].articlePrices[*].price`, absent becomes `0`, format `0.####` invariant - and the
  `LOADING_EQUIPMENT` filter still lives in `DilosRenderNode.RenderArticles`. Both must move into
  the preparation step, or byte parity is unreachable. This is exactly what the spec's adapter-half
  step 1 asks for; the brief dropped it. Task 2 carries it.
- `AsAiYamls_SftpUpload_ReadsTheRenderOutputAndTargetsTheLkvRoot` does
  `Assert.Single(nodes.OfType<DilosRenderNodeConfiguration>())` for every yaml that has an upload,
  BEFORE any path comparison. The as yaml keeps its upload and loses its `DilosRender@1` in Task 2,
  so that assertion throws for a reason that looks unrelated to the change. The test has to learn
  two delivery shapes.
- Real delivered reference files exist outside the repo:
  `LKV-Vorbereitung/g5delta-testdateien/AS20260820132736.txt` and `AS20260824104814.txt`, 5179 bytes
  each, 46 lines, 34 pipe fields per line, zero CR, final byte `0x0A`.
  The in-repo golden `tests/Lkv.WeClapp.Core.Tests/Fixtures/AS20240206020204.txt` (522 lines, zero
  CR) HAS a header row and is therefore a layout witness, not a delivery witness.

## Global Constraints

- `TreatWarningsAsErrors`, `Nullable enable`, `LangVersion latestmajor`, `net10.0`. An XML
  `<see cref="..."/>` pointing at a deleted type is a BUILD ERROR here, not a warning.
- Pre-commit gate, all three must pass, `-c Debug` (never `-c DebugL`, its local feed is stale):
  `dotnet format Octo.WeClappAdapter.slnx --verify-no-changes`,
  `dotnet build Octo.WeClappAdapter.slnx -c Debug`, `dotnet test Octo.WeClappAdapter.slnx -c Debug`.
  Release is run in addition at the end of every task and its numbers are reported.
- Every new `[Fact]`/`[Theory]` in `PipelineYamlContractTests` MUST be named in the repo
  `CLAUDE.md`, or `DocumentationContractTests.ClaudeMd_NamesEveryPipelineContractTest` reds the
  WHOLE suite from a place unrelated to the change.
- Node log calls are message templates with args, never interpolated strings.
- Commit subjects in English: `<type>(AB#4843): ...` for P7, `<type>(AB#4846): ...` for the C4 half,
  each with a `Co-Authored-By:` trailer. No review labels in code comments (no reviewer names, no
  "review finding", no M1/M2) - only the technical constraint. ASCII hyphens only, checked
  programmatically before each commit.
- TDD: every test is seen red first, and red for the right reason.
- NO push and NO pull request without Martin's explicit GO in this session. A peer session cannot
  grant it.

## File structure

| File | Responsibility after this plan |
|---|---|
| `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs` | DELETED (holds `WeClappFetchCore` + `IWeClappFetchConfiguration` too) |
| `src/AdapterMeshWeClapp/Nodes/WeClappFetchStepNode.cs` | DELETED |
| `src/AdapterMeshWeClapp/Nodes/DilosFileFetchTriggerNode.cs` | DELETED |
| `src/AdapterMeshWeClapp/Nodes/DilosFileFetchStepNode.cs` | DELETED |
| `src/AdapterMeshWeClapp/Nodes/DilosFileFetchCore.cs` | keeps only what `DilosFileGate@1` uses: `ScopePrefix(string,string,string)`, `FileKey(string,long,string)`, `Escape` |
| `src/AdapterMeshWeClapp/Services/DilosFileFetchState.cs` | unchanged behaviour; XML docs stop naming deleted types |
| `src/AdapterMeshWeClapp/Services/SftpFileSystem.cs` | seam shrinks to what survives; docs stop naming deleted nodes |
| `src/AdapterMeshWeClapp/WeClappHttpClientRegistration.cs` | default client + `WeClappArWriteNode` + `WeClappBeWriteNode` |
| `src/AdapterMeshWeClapp/Program.cs` | registers exactly 8 custom node types |
| `src/AdapterMeshWeClapp/Nodes/DilosExportRunKeyNode.cs` | ALSO emits the delivery file name from the same clock read |
| `src/AdapterMeshWeClapp/Nodes/WeClappResolveSupplySourcesNode.cs` | ALSO projects the EK price scalar and drops system articles |
| `src/AdapterMeshWeClapp/Nodes/DilosRenderNode.cs` | AI only |
| `src/Lkv.WeClapp.Core/Dilos/DilosArticleWriter.cs` | DELETED in Task 2 (its layout moves into the as yaml, its assertions into the parity test) |
| `src/Lkv.WeClapp.Core/Dilos/DilosFile.cs` | the AS name format stays here, now called from the export-run node |
| `pipelines/weclapp-articles-to-as.yaml` | preparation step, `RenderDelimitedText@1` with 34 columns, `If@1` empty gate, upload, marker |
| `tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt` | NEW frozen byte anchor: what the OLD path produced for the fixture batch |

---

### Task 3 (done first): P7 / AB#4843 - remove the four legacy fetch node types

**Files:**
- Delete: `src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs`,
  `src/AdapterMeshWeClapp/Nodes/WeClappFetchStepNode.cs`,
  `src/AdapterMeshWeClapp/Nodes/DilosFileFetchTriggerNode.cs`,
  `src/AdapterMeshWeClapp/Nodes/DilosFileFetchStepNode.cs`
- Delete: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs`,
  `.../WeClappFetchStepNodeTests.cs`, `.../DilosFileFetchTriggerNodeTests.cs`,
  `.../DilosFileFetchStepNodeTests.cs`
- Modify: `src/AdapterMeshWeClapp/Program.cs`,
  `src/AdapterMeshWeClapp/WeClappHttpClientRegistration.cs`,
  `src/AdapterMeshWeClapp/Nodes/DilosFileFetchCore.cs`,
  `src/AdapterMeshWeClapp/Nodes/DilosFileGateNode.cs` (docs),
  `src/AdapterMeshWeClapp/Nodes/DilosFileConfirmNode.cs` (docs),
  `src/AdapterMeshWeClapp/Services/DilosFileFetchState.cs` (docs),
  `src/AdapterMeshWeClapp/Services/SftpFileSystem.cs`,
  `src/AdapterMeshWeClapp/Services/SshNetSftpFileSystem.cs`
- Modify tests: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs`,
  `.../PipelineChainIntegrationTests.cs`, `.../WeClappCustomerSmokeTests.cs`,
  `.../AsExportGateTests.cs`, `.../AiExportGateTests.cs`,
  `.../Nodes/DilosFileFetchCoreTests.cs`, `.../Nodes/DefaultHttpClientRegistrationTests.cs`,
  `.../Nodes/DilosFileGateNodeTests.cs` (docs), `.../Services/DilosFileFetchStateTests.cs` (docs)
- Modify docs: `CLAUDE.md`, `README.md`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: a repository whose `Program.cs` registers exactly `WeClappToCkNode`,
  `DilosRenderNode`, `WeClappArWriteNode`, `WeClappBeWriteNode`,
  `WeClappResolveSupplySourcesNode`, `DilosExportRunKeyNode`, `DilosFileGateNode`,
  `DilosFileConfirmNode` - 8 types, 0 trigger nodes.

- [ ] **Step 1: Re-prove the precondition before deleting anything**

```bash
grep -rn "WeClappFetch@1\|WeClappFetchStep@1\|DilosFileFetch@1\|DilosFileFetchStep@1" pipelines/
```
Expected: no output. If anything matches, STOP - the deletion rule is not satisfied.

- [ ] **Step 2: Write the failing guard for the HTTP client registration**

The registration names its clients after node types. Deleting a node type must delete its client
entry too, and must NOT disturb the decompression the surviving clients depend on. Add to
`tests/AdapterMeshWeClapp.Tests/Nodes/DefaultHttpClientRegistrationTests.cs` a test that pins the
exact SET of configured client names:

```csharp
    // The registration names each client after the node type that resolves it, so deleting a node
    // must delete its entry. Pinning the exact SET - not just "the default one is configured" -
    // makes a leftover entry for a deleted node, or a lost entry for a surviving one, fail here
    // instead of at the first WeClapp call in the cluster. The deleted name is spelled as a
    // literal on purpose: its symbol is about to disappear.
    [Fact]
    public void AddWeClappHttpClients_ConfiguresExactlyTheDefaultAndTheTwoWriteClients()
    {
        var services = new ServiceCollection();
        services.AddWeClappHttpClients();
        var provider = services.BuildServiceProvider();
        var monitor = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        foreach (var name in new[] { string.Empty, nameof(WeClappArWriteNode), nameof(WeClappBeWriteNode) })
        {
            Assert.Equal(DecompressionMethods.All, PrimaryHandlerOf(monitor, name).AutomaticDecompression);
        }

        Assert.Empty(monitor.Get("WeClappFetchTriggerNode").HttpMessageHandlerBuilderActions);
    }
```

`PrimaryHandlerOf` is the existing builder-stub walk of the current test, extracted to a private
helper so both tests share it.

- [ ] **Step 3: Run it and watch it fail for the right reason**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~DefaultHttpClientRegistrationTests"
```
Expected: FAIL on the last assertion - `WeClappFetchTriggerNode` still has a handler action.

- [ ] **Step 4: Shrink the registration**

In `WeClappHttpClientRegistration.cs` drop `nameof(WeClappFetchTriggerNode)` from the name array and
reword the summary so it no longer promises a client per node for nodes that no longer exist.

- [ ] **Step 5: Run it and watch it pass**

Same command. Expected: PASS.

- [ ] **Step 6: Migrate the chain integration test off the deleted fetch node**

`PipelineChainIntegrationTests` uses `WeClappFetchTriggerNode` only to BUILD the document the real
chain then transforms. The shipped pipelines build that document with `MakeHttpRequest@1` and
`ForEach@1` instead. Replace phase 1 of both tests with the document shape those yamls actually
produce - the ai chain an object with `item` and `customer`, the as chain `{"items":[...]}` - built
as literal JSON in the test. Keep every phase-2 and phase-3 assertion byte-identical: they are the
value of these tests. Delete the `FakeHttpMessageHandler` / `IHttpClientFactory` scaffolding that
becomes unused and update the class summary so it no longer claims a fetch node is in the chain.

- [ ] **Step 7: Migrate the live customer smoke onto the standard fetch path**

`WeClappCustomerSmokeTests` must stay strictly read-only (GET only) and stay a no-op without
`WECLAPP_CUSTOMER_API_KEY` / `WECLAPP_CUSTOMER_BASEURL`. Replace `WeClappFetchTriggerNode` with a
direct `HttpClient` (handler `AutomaticDecompression = DecompressionMethods.All`, header
`AuthenticationToken`) issuing exactly the requests the shipped yamls issue:
`GET {baseUrl}/article?page=1&pageSize=10`, `GET {baseUrl}/salesOrder?page=1&pageSize=10`, and for
the first order carrying a `customerId`, `GET {baseUrl}/customer?id-eq={customerId}`. Assert what
the yamls depend on and nothing else: the `{"result":[...]}` envelope exists, articles carry a
non-empty `id`, and the `id-eq` lookup returns exactly that customer. Log counts only - never
payload contents and never the key.

- [ ] **Step 8: Run the two migrated suites**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~PipelineChainIntegrationTests|FullyQualifiedName~WeClappCustomerSmokeTests"
```
Expected: PASS. Record whether the smokes ran live or skipped - a skipped smoke proves compilation,
not the API contract.

- [ ] **Step 9: Delete the four node files and their four test files**

```bash
git rm src/AdapterMeshWeClapp/Nodes/WeClappFetchTriggerNode.cs \
       src/AdapterMeshWeClapp/Nodes/WeClappFetchStepNode.cs \
       src/AdapterMeshWeClapp/Nodes/DilosFileFetchTriggerNode.cs \
       src/AdapterMeshWeClapp/Nodes/DilosFileFetchStepNode.cs \
       tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchTriggerNodeTests.cs \
       tests/AdapterMeshWeClapp.Tests/Nodes/WeClappFetchStepNodeTests.cs \
       tests/AdapterMeshWeClapp.Tests/Nodes/DilosFileFetchTriggerNodeTests.cs \
       tests/AdapterMeshWeClapp.Tests/Nodes/DilosFileFetchStepNodeTests.cs
```

- [ ] **Step 10: Follow the compiler until it is silent**

Registrations in `Program.cs` (both `RegisterTriggerNode` calls, `RegisterNode<WeClappFetchStepNode>`,
`RegisterNode<DilosFileFetchStepNode>`) and in `PipelineYamlContractTests.DeserializePipeline`
(four `RegisterNodeConfiguration` calls); the two dead names in the credentials guard trigger; the
two `RegisterNodeConfiguration` lines each in `AsExportGateTests` / `AiExportGateTests`; every XML
`<see cref>` naming a deleted type. In `DilosFileFetchCore.cs` delete `GlobMatch`,
`ListMatchingFiles`, `FileKey(SftpFileEntry)`, `ScopePrefix(IDilosFileFetchConfiguration)` and the
`IDilosFileFetchConfiguration` interface - `DilosFileGate@1` uses only the three-string
`ScopePrefix` and the text `FileKey`. In `SftpFileSystem.cs` the seam shrinks to what still has a
caller. Move the escaping assertions of `DilosFileFetchCoreTests` onto the surviving three-string
`ScopePrefix` rather than deleting them; drop only the `ListMatchingFiles` guard test, whose
subject is gone.

- [ ] **Step 11: Re-prove the node inventory is exactly 8**

```bash
grep -c "RegisterNode<\|RegisterTriggerNode<" src/AdapterMeshWeClapp/Program.cs
grep -rn "\[NodeName(" src/AdapterMeshWeClapp/ --include=*.cs
```
Expected: 8 registrations and exactly 8 `[NodeName(...)]` declarations: `DilosExportRunKey`,
`DilosFileConfirm`, `DilosFileGate`, `DilosRender`, `WeClappArWrite`, `WeClappBeWrite`,
`WeClappResolveSupplySources`, `WeClappToCk`.

- [ ] **Step 12: Update `CLAUDE.md` and `README.md`**

`CLAUDE.md`: the project-structure sentence listing `WeClappFetchStep@1`, `DilosFileFetchStep@1`,
`WeClappFetch@1` and `DilosFileFetch@1` as "still registered but unused", and the AR/BE paragraph
that credits `DilosFileFetchStep@1` with the unconditional scope pruning - after this task that type
does not exist, so the accepted-residue note must describe the trade without naming a live type.
`README.md` node list likewise.

- [ ] **Step 13: Full gate, Debug and Release**

```bash
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes
dotnet build Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Release
```
Expected: all green. Record both totals; they must be the 373 baseline minus the deleted cases plus
the one added guard.

- [ ] **Step 14: Hygiene, then commit**

```bash
git diff --cached | grep -nP '[\x{2010}-\x{2015}\x{2212}]' && echo "NON-ASCII HYPHEN FOUND" || echo "hyphens clean"
git diff --cached | grep -niE 'review finding|reviewer|\bM1\b|\bM2\b' && echo "LABEL FOUND" || echo "labels clean"
```
Commit subject: `refactor(AB#4843): remove the retired WeClapp and DILOS fetch nodes`, with the
`Co-Authored-By` trailer.

---

### Task 1: the AS delivery file name moves to `DilosExportRunKey@1` (decision D3)

**Files:**
- Modify: `src/AdapterMeshWeClapp/Nodes/DilosExportRunKeyNode.cs`
- Modify: `src/Lkv.WeClapp.Core/Dilos/DilosFile.cs`
- Modify: `pipelines/weclapp-articles-to-as.yaml`
- Modify: `tests/AdapterMeshWeClapp.Tests/Nodes/DilosExportRunKeyNodeTests.cs`
- Modify: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs`
- Modify: `CLAUDE.md` (only if a contract test is added or renamed)

**Interfaces:**
- Consumes: nothing from Task 3.
- Produces: `$.meta` = `{ exportKind, exportDay, fileName }`, where `fileName` is
  `AS<yyyyMMddHHmmss>.txt` in Vienna local time, built from the SAME `GetUtcNow()` call as
  `exportDay`. Task 2 reads `$.meta.fileName` from `SftpUpload@1.fileNamePath`.

- [ ] **Step 1: Write the failing single-clock-read tests**

In `DilosExportRunKeyNodeTests`, driven by `FixedTimeProvider`:

```csharp
    // exportDay and the delivery file name must come from ONE clock read. With two reads a run
    // that crosses Vienna midnight stamps the file for day N+1 while the marker still says day N,
    // and because no marker exists for N+1 the next tick delivers the same day again.
    [Fact]
    public async Task ProcessObjectAsync_LateEveningVienna_DayAndFileNameAgree()
    {
        // 2026-08-28 22:30:15 Vienna (CEST, UTC+2) = 20:30:15 UTC
        Configure("AS", "$.meta", new DateTimeOffset(2026, 8, 28, 20, 30, 15, TimeSpan.Zero));

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var key = CapturedKey();
        Assert.Equal("2026-08-28", key["exportDay"]!.ToString());
        Assert.Equal("AS20260828223015.txt", key["fileName"]!.ToString());
    }
```

Plus the coupling itself, which is the property a second clock read would break, as a `[Theory]`
over the two instants either side of Vienna midnight: `2026-08-28T21:59:59Z` (= 23:59:59 Vienna) is
day `2026-08-28` with a name starting `AS20260828`, and `2026-08-28T22:00:00Z` (= 00:00:00 Vienna
on the 29th) moves BOTH to `2026-08-29` / `AS20260829`. Assert the day and the name's date part
against each other, not only against literals.

- [ ] **Step 2: Run them and watch them fail for the right reason**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~DilosExportRunKeyNodeTests"
```
Expected: FAIL because `key["fileName"]` is null - the node writes two properties today. Not a
compile error, not a wrong-date failure.

- [ ] **Step 3: Emit the file name from the existing clock read**

In `DilosExportRunKeyNode.ProcessObjectAsync`, hold the single `GetUtcNow()` result in a local and
derive both values from it:

```csharp
        var utcNow = _timeProvider.GetUtcNow();
        var viennaNow = TimeZoneInfo.ConvertTime(utcNow, ViennaTime.Zone);
        var key = new JsonObject
        {
            ["exportKind"] = config.ExportKind,
            ["exportDay"] = viennaNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["fileName"] = EnsurePlainFileName(DilosFile.DeliveryFileName(config.ExportKind, utcNow)),
        };
```

`DilosFile.AsFileName` is AS-specific while `ExportKind` is configurable, so generalise it in
`Lkv.WeClapp.Core/Dilos/DilosFile.cs` to
`public static string DeliveryFileName(string kind, DateTimeOffset utcNow)` - kind, Vienna
`yyyyMMddHHmmss`, `.txt` - and keep `AsFileName` as the AS-named caller of it so the AI/AS naming
stays in one place. Carry the path guard over from `DilosRenderNode.EnsurePlainFileName`: no `/`,
no `\`, no `..`. The guard is cheap and the kind comes from configuration.

- [ ] **Step 4: Run them and watch them pass**

Same command. Expected: PASS.

- [ ] **Step 5: Point the yaml at the new name and split the delivery-coupling guard**

In `pipelines/weclapp-articles-to-as.yaml`: extend the `DilosExportRunKey@1` comment to
`{ exportKind, exportDay, fileName }`, drop `fileNameTargetPath` from the `DilosRender@1` step, and
change `SftpUpload@1.fileNamePath` to `$.meta.fileName`. `encoding: iso-8859-1` and
`onEncodingError: Replace` stay untouched.

`AsAiYamls_SftpUpload_ReadsTheRenderOutputAndTargetsTheLkvRoot` asserts
`render.FileNameTargetPath == upload.FileNamePath` for BOTH deliveries. For the as yaml that
coupling now runs from the export-run node to the upload. Split it per yaml: the ai delivery keeps
the render coupling; the as delivery asserts `upload.FileNamePath` equals the single
`DilosExportRunKeyNodeConfiguration`'s `TargetPath` plus `.fileName`, read off the real
configuration in that yaml. The content coupling (`render.TargetPath == upload.Path`), the
`deliveries == 2` count and the single-SFTP-entry assertion stay exactly as they are.

- [ ] **Step 6: Prove the guard by mutation**

Set `fileNamePath: $.meta.wrongName` in the as yaml, run the contract suite, expect FAIL naming the
as yaml. Revert, re-run, expect PASS. Record both - without the red run the green one proves
nothing.

- [ ] **Step 7: Full gate, Debug and Release, hygiene, then commit**

```bash
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes
dotnet build Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Release
```
Commit subject: `feat(AB#4846): stamp the AS delivery name from the export-run clock read`, with the
`Co-Authored-By` trailer.

---

### Task 2: the AS delivery renders through `RenderDelimitedText@1`

**Files:**
- Create: `tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt` (frozen output of the OLD
  path for the fixture batch - the byte anchor)
- Modify: `src/AdapterMeshWeClapp/Nodes/WeClappResolveSupplySourcesNode.cs`
- Modify: `src/AdapterMeshWeClapp/Nodes/DilosRenderNode.cs` (AI only, gated on Step 14's decision)
- Delete (same gate): `src/Lkv.WeClapp.Core/Dilos/DilosArticleWriter.cs` and
  `tests/Lkv.WeClapp.Core.Tests/DilosArticleWriterTests.cs`
- Modify: `pipelines/weclapp-articles-to-as.yaml`
- Modify: `tests/AdapterMeshWeClapp.Tests/Nodes/WeClappResolveSupplySourcesNodeTests.cs`,
  `.../Nodes/DilosRenderNodeTests.cs`, `.../PipelineYamlContractTests.cs`,
  `.../PipelineChainIntegrationTests.cs`
- Modify: `CLAUDE.md` (node list, AS delivery section, contract-test inventory)

**Interfaces:**
- Consumes: `$.meta.fileName` from Task 1.
- Produces: `WeClappResolveSupplySources@1` writes to `$.items` an array of article objects with
  system articles removed, each carrying the extra scalar property `ekPreis` (string, DILOS format).
  `RenderDelimitedText@1` reads `$.items` and writes one string to `$.dilosAs`.

- [ ] **Step 0: Force a fresh package resolution and prove the node is there**

```bash
dotnet restore Octo.WeClappAdapter.slnx --force --no-cache
grep -o '"Meshmakers.Octo.MeshAdapter.Nodes/[^"]*"' src/AdapterMeshWeClapp/obj/project.assets.json | sort -u
```
Expected: `3.4.101` or newer, and `RenderDelimitedTextNodeConfiguration` resolvable from
`Meshmakers.Octo.MeshAdapter.Nodes.Transform`.

- [ ] **Step 1: Freeze the byte anchor from the OLD path, before touching anything**

Write a test that renders a fixture batch through TODAY's path and compares it byte-for-byte to a
checked-in expected file. The batch is a literal JSON array in the test built from
`tests/Lkv.WeClapp.Core.Tests/Fixtures/article.json` (a real `GET /article` envelope: one
`LOADING_EQUIPMENT` article that must be dropped, one `STORABLE` article without a price) plus a
third article carrying a resolved supply-source price, so column 20 is exercised in both states -
`0` from absence, a real value from a price - and an umlaut in a name, so the Latin-1 delivery
assertion has something to bite on.

```csharp
    // Byte anchor for the 34-column AS layout: the expected file is what the pre-swap renderer
    // produced for this batch. Compared as BYTES, so a column that moves, a delimiter that changes
    // or a lost trailing newline fails here. The invariants are asserted against the file itself
    // as well - 34 fields on every line, zero CR, final byte 0x0A - so the anchor certifies the
    // real delivery shape rather than merely agreeing with itself.
    [Fact]
    public async Task AsBatch_RendersTheFrozenThirtyFourColumnLayout()
```

Then check the COMMITTED blob, not the working tree:

```bash
git add tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt
git cat-file -p :tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt | wc -c
git cat-file -p :tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt | tr -cd '\r' | wc -c
```
Expected: byte count equal to the working-tree file, CR count 0.

- [ ] **Step 2: Run it and watch it pass on the OLD path**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~AsBatch_RendersTheFrozenThirtyFourColumnLayout"
```
Expected: PASS. This is the one step in the plan where a test is green before the change - it is the
reference measurement, and the swap below is what has to keep it green.

- [ ] **Step 3: Write the failing preparation-step tests**

In `WeClappResolveSupplySourcesNodeTests`:

```csharp
    // Column 20 is the only AS column that needs a rule, and the rule is WeClapp: the first
    // parseable supplySources[*].articlePrices[*].price, absent becomes 0, formatted 0.#### with
    // the invariant culture. A column model cannot express that, so the step that already touches
    // the articles projects it as a finished scalar.
    [Fact]
    public async Task ProcessObjectAsync_ProjectsThePurchasePriceAsADilosScalar()

    [Fact]
    public async Task ProcessObjectAsync_ArticleWithoutSupplySourcePrice_ProjectsZero()

    // System articles (loading equipment such as pallets) never belong in the article master
    // delivery. The render used to drop them; a column model cannot, so the preparation does.
    [Fact]
    public async Task ProcessObjectAsync_DropsSystemArticles()
```

- [ ] **Step 4: Run them and watch them fail for the right reason**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~WeClappResolveSupplySourcesNodeTests"
```
Expected: FAIL - `ekPreis` is absent and the `LOADING_EQUIPMENT` article is still in the array.

- [ ] **Step 5: Implement the preparation step**

In `WeClappResolveSupplySourcesNode`, after the supply sources of an article are resolved, read the
article through the `WeClappArticle` DTO the value rules are written against and set
`item["ekPreis"] = WeClappToDilos.Num(WeClappToDilos.EkPreis(article.PurchasePrice))`; in the loop
that builds `enriched`, skip articles for which `WeClappToDilos.IsSystemArticle` holds and log the
skipped count with a message template. Both rules stay in `Lkv.WeClapp.Core` where they live today -
only the call site moves.

- [ ] **Step 6: Run them and watch them pass**

Same command. Expected: PASS.

- [ ] **Step 7: Swap the yaml**

Replace the `DilosRender@1` step of `pipelines/weclapp-articles-to-as.yaml` with:

```yaml
      - type: RenderDelimitedText@1
        description: Render the article master lines (DILOS AS layout, _specs/AS.md field order)
        path: $.items
        targetPath: $.dilosAs
        delimiter: "|"
        lineEnding: Lf                  # NEVER omit: golden AS files are pure LF
        trailingNewLine: true           # the delivered file ends on 0x0A
        onDelimiterInValue: Fail        # DILOS has no escaping - a pipe inside a value shifts every
                                        # following column and the LKV import notices nothing
        columns:
          - value: "A*"                 #  1 Satzart
          - {}                          #  2 Kennzeichen
          - valuePath: $.id             #  3 Artikelnummer
            required: true              #    the one mandatory field per _specs/AS.md
          - valuePath: $.name           #  4 Bezeichnung 1
          - valuePath: $.articleNumber  #  5 Bezeichnung 2 (SKU)
          - {}                          #  6 Artikelgruppe 1
          # ... all 34 entries, one per DILOS field, in order ...
```

All 34 entries are written out, each with its DILOS field number and name as a trailing comment.
Populated: 1 (`A*`), 3 (`$.id`), 4 (`$.name`), 5 (`$.articleNumber`), 11 (`$.ean`),
12 (`$.unitName`), 20 (`$.ekPreis`), 23 (`1`). The other 26 are `{}`.

Then wrap the delivery in the empty gate (decision D5b), so an empty batch cannot upload a zero-byte
file and burn the day's marker:

```yaml
      - type: If@1
        description: Deliver only when the render produced content (an empty batch must not burn the day)
        path: $.dilosAs
        operator: NotEqual
        value: ""
        valueType: String
        transformations:
          - type: SftpUpload@1
            ...
          - type: CreateUpdateInfo@1
            ...
          - type: ApplyChanges@2
            ...
```

- [ ] **Step 8: Teach the delivery-coupling guard two delivery shapes**

`AsAiYamls_SftpUpload_ReadsTheRenderOutputAndTargetsTheLkvRoot` does
`Assert.Single(nodes.OfType<DilosRenderNodeConfiguration>())` for every yaml with an upload; after
this swap the as yaml has an upload and no `DilosRender@1`, so it throws before comparing anything.
Rewrite the per-yaml body to resolve the content source as EITHER the single `DilosRender@1` (ai) OR
the single `RenderDelimitedText@1` (as), require exactly one of the two shapes, and keep both
existing comparisons plus `deliveries == 2` and the single-SFTP-entry assertion unchanged. A yaml
with neither, or with both, is a violation.

- [ ] **Step 9: Add the empty-gate contract test (the D5b condition)**

```csharp
    // The render writes an empty string for an empty batch and calls next; SftpUpload@1 would
    // upload that as a 0-byte file and the marker behind it would burn the Vienna day. The brake is
    // this gate, and it only works while its path is EXACTLY the render's targetPath: If@1 reads an
    // absent path as null, and null != "" is true, so a mistyped path lets the empty delivery
    // straight through.
    [Fact]
    public async Task AsYaml_EmptyRenderOutput_IsGatedBeforeTheDeliveryAndTheMarker()
```
Assert: the gate is an `If@1` with operator NotEqual, value `""`, valueType String and `Path` equal
to the `RenderDelimitedText@1` `TargetPath`, and that `SftpUpload@1`, `CreateUpdateInfo@1` and
`ApplyChanges@2` all sit INSIDE it.

- [ ] **Step 10: Add the column-layout contract test**

```csharp
    // The 34-column layout now lives in the yaml, so the yaml is what has to be pinned: 34 columns
    // in order, the eight populated positions on their exact DILOS field numbers, every other
    // column empty, and the delimiter / line-ending / trailing-newline triple the golden files show.
    [Fact]
    public async Task AsYaml_RenderDelimitedText_SpellsOutTheThirtyFourColumnDilosLayout()
```

- [ ] **Step 11: Name every new contract test in `CLAUDE.md`**

Otherwise `DocumentationContractTests.ClaudeMd_NamesEveryPipelineContractTest` reds the whole suite
from a place unrelated to the change. Add
`AsYaml_EmptyRenderOutput_IsGatedBeforeTheDeliveryAndTheMarker` and
`AsYaml_RenderDelimitedText_SpellsOutTheThirtyFourColumnDilosLayout` to the guard inventory with one
sentence each on what they prevent, and update the AS/AI delivery section: the render node no longer
owns the AS content guard, the yaml's `If@1` does.

- [ ] **Step 12: Re-point the byte anchor at the new composition**

Change the parity test from Step 1 to drive the REAL shipped configurations: deserialize
`weclapp-articles-to-as.yaml`, pull the `WeClappResolveSupplySources@1` and `RenderDelimitedText@1`
configurations out of it, run the real nodes over the fixture batch in a real `DataContextImpl` and
compare the produced string to the SAME expected fixture bytes. The fixture is NOT regenerated - it
is the frozen record of what the old path produced, and that is the parity claim.

- [ ] **Step 13: Run it, then prove it by mutation**

```bash
dotnet test Octo.WeClappAdapter.slnx -c Debug --filter "FullyQualifiedName~AsBatch_RendersTheFrozenThirtyFourColumnLayout"
```
Expected: PASS - byte parity with the old renderer.

Then mutate the yaml and re-run, each time expecting FAIL, each time reverting: swap the `$.name`
and `$.articleNumber` columns; delete one `{}` column so the row has 33 fields; set
`trailingNewLine: false`; set `lineEnding: CrLf`. Record each red.

- [ ] **Step 14: Shrink `DilosRender@1` to AI - ASK MARTIN FIRST**

The spec's adapter-half step 2 says the AS branch, the article writer, the AS file name and
`mode: AS` go. The session brief only says the node stays in the repo. Removing the branch ends the
second source of truth for the 34 columns; keeping it means a yaml-only rollback to the pre-swap as
pipeline stays possible, because the running image would still know `mode: AS`. This is an
operational call, not a code-style one. Ask before executing.

If removing: delete the `AS` case, `RenderArticles`, the AS branch of the file-name switch and the
AS half of the empty-content guard from `DilosRenderNode`; delete `DilosArticleWriter` and its tests,
whose assertions now live in the parity and column-layout tests; update the node summary; update
`DilosRenderNodeTests` and the as half of `PipelineChainIntegrationTests`.

- [ ] **Step 15: Full gate, Debug and Release, hygiene, then commit**

```bash
dotnet format Octo.WeClappAdapter.slnx --verify-no-changes
dotnet build Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Debug
dotnet test  Octo.WeClappAdapter.slnx -c Release
git diff --cached | grep -nP '[\x{2010}-\x{2015}\x{2212}]' && echo "NON-ASCII HYPHEN FOUND" || echo "hyphens clean"
```
Commit subject: `feat(AB#4846): render the AS article master through RenderDelimitedText@1`, with the
`Co-Authored-By` trailer.

---

## Final verification (superpowers:verification-before-completion)

- [ ] Full suite green in Debug AND Release, both totals reported against the 373 baseline.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] Node inventory is 8, listed by name.
- [ ] Byte anchor checked at the COMMITTED blob, not the working tree.
- [ ] Every core claim has a recorded red run (mutation proof).
- [ ] No push, no PR - the diff goes to the review gate in a foreign session, then to Martin's GO.
