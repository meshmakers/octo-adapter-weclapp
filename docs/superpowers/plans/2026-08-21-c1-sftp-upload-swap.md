# C1: DILOS delivery on the standard SftpUpload node - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the AS and AI files through the product's `SftpUpload@1` node configured for ISO-8859-1 instead of the custom `DilosSftpWrite@1`, and retire the last `ApplyChanges@1` usage - both pinned by contract tests so neither can silently regress.

**Architecture:** The shipped YAMLs move the AS/AI transport to `SftpUpload@1` against the SDK version already running on staging (r3.4.93), and `DilosRender@1` takes over the safeguards the retired transport used to provide: it rejects path-carrying file names in both modes, refuses to emit an empty AI file, and ends an empty AS batch without a delivery. No chart change, no new custom node. `SftpUpload@1` gained `encoding` and `onEncodingError` in r3.4.89 (the `SftpUpload*` files are byte-identical from r3.4.89 to r3.4.93), so the swap does not raise the image floor: the binding floor of all five YAMLs stays r3.4.91, set by the ar/be `continueOnError`. With `encoding: iso-8859-1` and `onEncodingError: Replace` its behaviour matches `DilosSftpWrite@1` character for character (verified against `SftpContentEncoder` at tag r3.4.93: one `?` per Unicode scalar, a surrogate pair collapsing to one, a warning naming the offending code points). It additionally normalizes NFD to NFC before replacing, so decomposed umlauts survive where the custom node would have written `u?`. After this stage `DilosSftpWrite@1` has no remaining caller; deleting its code rides the next train, not this branch.

**Tech Stack:** OctoMesh pipeline YAML (strict deserializer), .NET 10, xUnit contract tests over the shipped YAMLs.

**Spec:** `C:\Users\martin-lt\Development\LKV-Vorbereitung\PLAN-SESSIONS-BIS-GO-LIVE-2026-08-20.md`, section "C1 - SftpUpload-Swap + YAML-Kleinkram"

## Global Constraints

- Target runtime is the SDK on staging: **r3.4.93**. Only properties that exist there may appear in YAML - `encoding` and `onEncodingError` do, verified in `SftpUploadNodeConfiguration` at that tag and at r3.4.89, where they first shipped. The floor an image must clear for the shipped YAMLs is **r3.4.91** (ar/be `continueOnError`), not r3.4.93.
- The DILOS file contract is unchanged: **ISO-8859-1, no BOM, LF line endings, byte-identical to the golden sample**. Rendering stays with `DilosRender@1`; only the transport node changes.
- **Guard-test rule (live since PR #15):** every new `[Fact]`/`[Theory]` in `PipelineYamlContractTests` must be named in this repo's `CLAUDE.md`, or `DocumentationContractTests.ClaudeMd_NamesEveryPipelineContractTest` turns the whole suite red. Add the documentation line in the same step as the test.
- The local pre-commit gate runs `-c Debug`. `-c DebugL` resolves against a stale local NuGet feed and produces false failures.
- Code comments carry the constraint, never a ticket or review reference. Developer-facing artifacts are written in English.
- No `ImportRt`, no `DeployTriggers`, no push, no PR without an explicit GO.

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `pipelines/weclapp-articles-to-as.yaml` | AS export (articles -> LKV) | Modify: delivery node + header comment |
| `pipelines/weclapp-orders-to-ai.yaml` | AI export (orders -> LKV) | Modify: delivery node + preceding comment |
| `pipelines/weclapp-articles-to-ck.yaml` | Article import into CK | Modify: `ApplyChanges@1` -> `@2` |
| `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs` | Contract guards over shipped YAMLs | Modify: two new guards |
| `CLAUDE.md` | Node inventory + machine-checked guard list | Modify: guard list, node lists |
| `README.md` | Pipeline overview | Modify: delivery node name |

**Not in scope, deliberately:** deleting `DilosSftpWriteNode.cs` and its tests (rides the next train once staging proves the swap), host-key pinning (product option arriving with C2), and the ar/be comment fix from the plan text - already correct in both files (`needs SDK >= r3.4.91`, verified 21.08.).

---

### Task 1: AS and AI deliver through SftpUpload@1 in ISO-8859-1

**Files:**
- Modify: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs` (new guard)
- Modify: `CLAUDE.md` (guard list + node inventory lines 22, 80)
- Modify: `pipelines/weclapp-articles-to-as.yaml:17-18` (comment), `:67-72` (node)
- Modify: `pipelines/weclapp-orders-to-ai.yaml:133-140` (comment + node)
- Modify: `README.md:15`
- Modify: `tests/AdapterMeshWeClapp.Tests/AsExportGateTests.cs`, `AiExportGateTests.cs` (added
  during execution: both pin the delivery node BY TYPE - "inside the gate, after render", not
  on the top level, not per item - so the swap turns them red until the type reference follows.
  Their `RegisterNodeConfiguration<DilosSftpWriteNodeConfiguration>()` lines GO: once the yamls
  they read carry no `DilosSftpWrite@1`, the registration serves nothing here. The new guard in
  `PipelineYamlContractTests` does need the type registered - to report a readable violation
  instead of a deserializer exception - and registers it in its own `DeserializePipeline`.)

**Interfaces:**
- Consumes: `SftpUploadNodeConfiguration` (`Encoding`, `OnEncodingError`, `ServerConfiguration`, `RemoteDirectory`, `FileNamePath`, inherited `Path`), `EncodingErrorHandling.Replace`, both in `Meshmakers.Octo.MeshAdapter.Nodes.Load` - already imported by the test file.
- Produces: guard `AsAiYamls_DeliverViaSftpUploadInIso88591`, referenced by name from `CLAUDE.md`.

- [ ] **Step 1: Document the new guard in CLAUDE.md first**

Without this line the whole suite fails for an unrelated reason and the red test in step 3 proves nothing. Append to the guard-test paragraph (after the `OrdersToAiYaml_CustomerNameUpdate_ResolvesForB2cCustomers` sentence):

```markdown
`AsAiYamls_DeliverViaSftpUploadInIso88591` pins the AS/AI delivery to `SftpUpload@1` with
`encoding: iso-8859-1` and `onEncodingError: Replace` and forbids `DilosSftpWrite@1` anywhere -
the node's encoding default is utf-8, so an omitted property would ship mojibake to LKV without
failing anything.
```

- [ ] **Step 2: Write the failing guard**

Append to `PipelineYamlContractTests.cs`, before the closing brace, keeping the file's `// ---------- contract N: ... ----------` banner style:

```csharp
    // ---------- contract 13: AS/AI deliver through the product node in Latin-1 ----------

    // The DILOS file format is ISO-8859-1 and SftpUpload@1 defaults to utf-8, so a delivery
    // node that loses the property writes umlauts as two bytes and LKV's import sees mojibake -
    // silently, because nothing fails. Replace keeps the historic behaviour of one '?' per
    // unrepresentable scalar; Fail would drop a whole day's delivery over a single character.
    [Fact]
    public async Task AsAiYamls_DeliverViaSftpUploadInIso88591()
    {
        var violations = new List<string>();
        var uploads = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);

            foreach (var legacy in Walk(root.Transformations).OfType<DilosSftpWriteNodeConfiguration>())
            {
                violations.Add($"{yaml}: '{legacy.Description}' still delivers via DilosSftpWrite@1 - " +
                               "the product node covers this since r3.4.89");
            }

            foreach (var upload in Walk(root.Transformations).OfType<SftpUploadNodeConfiguration>())
            {
                uploads++;

                if (!string.Equals(upload.Encoding, "iso-8859-1", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' writes '{upload.Encoding}', " +
                                   "expected 'iso-8859-1' - the utf-8 default corrupts umlauts in DILOS files");
                }

                if (upload.OnEncodingError != EncodingErrorHandling.Replace)
                {
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' uses " +
                                   $"onEncodingError '{upload.OnEncodingError}', expected 'Replace' - " +
                                   "Fail would suppress the whole delivery over one character");
                }
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, uploads); // as + ai; a third delivery must be a deliberate edit here
    }
```

- [ ] **Step 3: Run it and confirm it fails for the right reason**

```bash
dotnet test tests/AdapterMeshWeClapp.Tests -c Debug --filter "FullyQualifiedName~AsAiYamls_DeliverViaSftpUploadInIso88591" --logger "trx;LogFileName=c1-step3.trx"
```

Expected: FAIL, two violations naming `DilosSftpWrite@1` plus `Assert.Equal(2, uploads)` seeing 0. Parse the TRX rather than grepping stdout - a piped `grep` has committed on red twice in this repo.

- [ ] **Step 4: Swap the AS delivery node**

In `pipelines/weclapp-articles-to-as.yaml`, replace the header comment (lines 17-18):

```yaml
# Encoding: delivered as ISO-8859-1 (DILOS file format); SftpUpload@1 defaults to utf-8,
# so the encoding property below is what keeps umlauts single-byte.
```

and the node (lines 67-72):

```yaml
      - type: SftpUpload@1
        description: Deliver the AS file to the LKV SFTP root as ISO-8859-1
        serverConfiguration: LkvSftp        # SAME tenant GlobalConfiguration as the return path
        remoteDirectory: /
        fileNamePath: $.dilosAsFileName
        path: $.dilosAs
        encoding: iso-8859-1
        onEncodingError: Replace
```

- [ ] **Step 5: Swap the AI delivery node**

In `pipelines/weclapp-orders-to-ai.yaml`, replace the comment and node (lines 133-140):

```yaml
          # ISO-8859-1 delivery (DILOS file format): SftpUpload@1 defaults to utf-8, the
          # encoding property keeps umlauts single-byte; characters outside Latin-1 become
          # a single '?' each and are logged with their code points.
          - type: SftpUpload@1
            description: Deliver the AI file to the LKV SFTP root as ISO-8859-1 (delete-side handled by LKV)
            serverConfiguration: LkvSftp        # SAME tenant GlobalConfiguration as the return path
            remoteDirectory: /
            fileNamePath: $.dilosAiFileName
            path: $.dilosAi
            encoding: iso-8859-1
            onEncodingError: Replace
```

- [ ] **Step 6: Run the guard and then the whole suite**

```bash
dotnet test tests/AdapterMeshWeClapp.Tests -c Debug --filter "FullyQualifiedName~AsAiYamls_DeliverViaSftpUploadInIso88591" --logger "trx;LogFileName=c1-step6a.trx"
dotnet test tests/AdapterMeshWeClapp.Tests -c Debug --logger "trx;LogFileName=c1-step6b.trx"
```

Expected: both PASS. The full run matters because the strict deserializer, the ForEach guards and the export-gate tests all read the same two YAMLs.

- [ ] **Step 7: Update the node inventories**

`README.md:15`: `DilosSftpWrite@1` (ISO-8859-1 delivery) -> `SftpUpload@1` (ISO-8859-1 delivery).
`CLAUDE.md:22`: same replacement inside the node list.
`CLAUDE.md:80`: read the sentence first - it is the `$.loopResult` contract, and its list names the `ForEach@1` children that write through the data context. That list is about shipped YAML usage, so the delivery node named there becomes `SftpUpload@1`.

- [ ] **Step 8: Commit**

```bash
git add pipelines/weclapp-articles-to-as.yaml pipelines/weclapp-orders-to-ai.yaml tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs CLAUDE.md README.md
git commit -m "AB#4846: deliver AS and AI through SftpUpload@1 in ISO-8859-1"
```

---

### Task 2: Retire the last ApplyChanges@1

**Files:**
- Modify: `tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs` (new guard)
- Modify: `CLAUDE.md` (guard list)
- Modify: `pipelines/weclapp-articles-to-ck.yaml:69-71`

**Interfaces:**
- Consumes: `ApplyChangesNodeConfiguration` (v1, `Path`) and `ApplyChangesNodeConfiguration2` (`EntityUpdatesPath`, `AssociationUpdatesPath`). `@2` derives from `NodeConfiguration`, not from `@1`, so `OfType<ApplyChangesNodeConfiguration>()` matches v1 only - confirm the two type names resolve in the test file's existing usings before writing the guard.
- Produces: guard `AllPipelineYamls_ApplyChanges_IsVersion2`.

- [ ] **Step 1: Document the guard in CLAUDE.md**

```markdown
`AllPipelineYamls_ApplyChanges_IsVersion2` forbids the deprecated `ApplyChanges@1` - its
configuration is a bare record with no association property at all, so adding an
`associationUpdatesPath` there does not drop associations quietly: the strict deserializer
rejects the unknown property and the pipeline registration fails at the tenant. The guard moves
that failure earlier, into this suite and next to the edit.
```

- [ ] **Step 2: Write the failing guard**

```csharp
    // ---------- contract 14: entity persistence uses the current ApplyChanges ----------

    // ApplyChanges@1 is a bare record with a single `path` - it has no property for association
    // updates at all. Adding an associationUpdatesPath to such a node therefore does not lose the
    // associations quietly: the strict deserializer rejects the unknown property and the pipeline
    // registration fails at the tenant. This guard moves that failure EARLIER and closer to the
    // edit - red in this suite instead of red on a deploy nobody is watching.
    [Fact]
    public async Task AllPipelineYamls_ApplyChanges_IsVersion2()
    {
        var violations = new List<string>();

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);

            foreach (var legacy in Walk(root.Transformations).OfType<ApplyChangesNodeConfiguration>())
            {
                violations.Add($"{yaml}: '{legacy.Description}' uses the deprecated ApplyChanges@1 - " +
                               "use @2 with entityUpdatesPath (and associationUpdatesPath when needed)");
            }
        }

        Assert.Empty(violations);
    }
```

- [ ] **Step 3: Run it and confirm it fails**

```bash
dotnet test tests/AdapterMeshWeClapp.Tests -c Debug --filter "FullyQualifiedName~AllPipelineYamls_ApplyChanges_IsVersion2" --logger "trx;LogFileName=c1-step2-3.trx"
```

Expected: FAIL with one violation naming `weclapp-articles-to-ck.yaml`.

- [ ] **Step 4: Swap the node**

`pipelines/weclapp-articles-to-ck.yaml:69-71` - the property is renamed, not just the version:

```yaml
      - type: ApplyChanges@2
        description: Persist the article upsert
        entityUpdatesPath: $.updates
```

- [ ] **Step 5: Run the guard and the whole suite**

```bash
dotnet test tests/AdapterMeshWeClapp.Tests -c Debug --logger "trx;LogFileName=c1-step2-5.trx"
```

Expected: PASS, including `AllPipelineYamls_EveryAttributeUpdate_DeclaresValueType`, which walks this node.

- [ ] **Step 6: Commit**

```bash
git add pipelines/weclapp-articles-to-ck.yaml tests/AdapterMeshWeClapp.Tests/PipelineYamlContractTests.cs CLAUDE.md
git commit -m "AB#4846: move the article upsert to ApplyChanges@2"
```

---

### Task 3: Prove it on staging (GO-gated, one step at a time)

No code. Each command needs an explicit GO; the import is the first state change on staging since the observation nights ended.

**Preconditions to verify first, read-only:**

- [ ] **Step 1: Confirm what staging actually runs**

The YAML uses properties introduced in r3.4.89. Verify the deployed adapter offers them rather than trusting the pin:

```
octo-cli -c GetPipelineSchema --adapterId <adapterRtId> --outputFile schema-staging.json
```

Expected: `SftpUpload@1` lists `encoding` and `onEncodingError` in its properties. If it does not, stop - the swap would fail deserialization on deploy.

- [ ] **Step 2: Import the pipelines (GO required)**

Import the changed YAMLs the way the runbook prescribes for a pipeline-only change. `as` and `ai` stay `Enabled: false`; nothing about triggers changes, so `DeployTriggers` is not part of this step.

- [ ] **Step 3: Run the AS export once, deploy-free (GO required)**

Follow `DREHBUCH-G5D-AS-PROBELAUF-2026-08-18.md` unchanged - the same `ExecutePipeline` mechanics that produced the B2 evidence, now exercising `SftpUpload@1`. The K1 day gate is open for 21.08.; a second attempt on the same day needs the marker cleared, which is its own GO.

- [ ] **Step 4: Compare bytes against the golden sample**

Fetch the produced `AS<timestamp>.txt` over SFTP and compare with `AS20260820132736.txt` from the B2 run:

```bash
cmp <new-file> <b2-reference>
```

Expected: identical except the timestamp inside the file name. Also confirm 5.179 bytes, ISO-8859-1, 34 fields, 46 lines, LF. Byte identity is the acceptance criterion - `GetPipelineStatus` proves nothing about a live registration, and `LastExecutionAt` after a manual run is not evidence of a cron tick.

- [ ] **Step 5: Record the result**

Update the runbook's verify expectations and the plan's status board. The Phase-4 pin only moves once this stage is fully proven.

---

### Task 4: Review gate and PR (after Martin's OK)

- [ ] **Step 1: Independent review of the finished diff**

Dispatch one reviewer with a fresh context and an explicit model, reviewing `git diff main...c1-sftp-upload-swap` against this plan. Subagents inherit the caller's model when none is given, which would make author and reviewer the same model.

- [ ] **Step 2: Work in the findings, then ask for the PR**

Push and PR only on Martin's explicit OK. PR body in English, plain hyphens only, no generated-with footer. The body should say why the custom node existed at all (Latin-1 before the product node could do it) and what now replaces it.

## Self-Review

- **Spec coverage:** SftpUpload swap (Task 1), `ApplyChanges@1` -> `@2` (Task 2), staging verify (Task 3). The spec's fourth item - the ar/be comment fix from r3.4.90 to r3.4.91 - is already in both files, recorded above under "Not in scope".
- **Placeholders:** none; every step carries its command or its YAML/C# body. The one deliberate judgement call is `CLAUDE.md:80` in Task 1 Step 7, where the surrounding sentence decides the edit - the step says how to decide.
- **Type consistency:** `SftpUploadNodeConfiguration.Encoding` is `string`, `OnEncodingError` is `EncodingErrorHandling`; `DilosSftpWriteNodeConfiguration` and `ApplyChangesNodeConfiguration` are the v1 types; `ApplyChangesNodeConfiguration2` exposes `EntityUpdatesPath`. All verified in source at tag r3.4.93 / on main.
