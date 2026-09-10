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
  repositories) + all custom pipeline nodes (outbound:
  `WeClappResolveSupplySources@1`, `DilosRender@1` (AI only; the AS article master renders
  through the product's `RenderDelimitedText@1`, and the CK-shaped view of articles and orders is
  built by standard nodes in the yamls) - the fetching itself is
  the product's `MakeHttpRequest@1` and the delivery its `SftpUpload@1`, see "AS/AI Delivery"
  below; return path: `WeClappArWrite@1`, `WeClappBeWrite@1` — the listing, the reading and the
  deleting are the product's `SftpList@1`, `SftpDownload@1` and `SftpDelete@1`, and the per-file
  state is an `Industry.Logistics/InboundFile` marker built by standard nodes in the yamls, see
  "AR/BE Return Path" below. That is the complete inventory: FOUR
  declared node types, and no trigger node of its own - every pipeline is driven by a passive
  product trigger, see "Pipeline Trigger Architecture" below)
- `src/Lkv.WeClapp.Core/` - plain core lib: WeClapp DTOs/JSON, WeClapp→DILOS value rules,
  DILOS AI writer (the AS article master is a column list in the yaml now), DILOS AR/BE parsers
  + write-back planners (fail-loud, golden-file verified)
- `src/charts/octo-weclapp-adapter/` - Helm chart (deployed by the Communication Operator;
  httpGet probes on `/healthz/live|ready`)
- `pipelines/` - tenant pipeline YAMLs (orders→AI per order; articles split into per-item
  CK sync + batched AS delivery [at most one file per Vienna calendar day, gated on the per-day
  CK marker `Industry.Logistics/ExportRun` whose key the yaml's own `DateTime@1` chain writes -
  K1 gate];
  AR/BE return path);
  the YAMLs carry no credentials — WeClapp access comes
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
- Custom nodes live in THIS repo (official adapter guideline), not in octo-mesh-adapter
- Primary constructors with DI (C# 12+)
- Observability is mandatory: `builder.AddObservability().AddSystemContextHealthCheck()` +
  `app.MapObservability()` — the chart's probes hit `/healthz/live|ready`; without the
  mapping the pod never becomes ready
- Node logs are message templates with args (`nodeContext.Info("... {0}", x)`) — NEVER
  interpolated strings: `INodeContext` forwards to structured logging, so a literal `{...}`
  (JSON body, URL) corrupts the template

## Pipeline Trigger Architecture
All 5 pipeline YAMLs carry two passive triggers — `FromPipelineTriggerEvent@1` (cron,
subscribes a per-pipeline queue and calls `ExecuteAsync` directly) and
`FromExecutePipelineCommand@1` (manual/API run, e.g. for Härtetest probing). Neither is a poll
loop: a redeploy or pod restart fires no execution. A fetch step
(`MakeHttpRequest@1` outbound, `SftpList@1` on the AR/BE return path) runs
first and seeds the data context at a fixed root path — always the array, even `[]` (a missing/non-array path aborts a downstream
`ForEach@1` with `PathMustBeArray`); in 4 of the 5 YAMLs a per-item `ForEach@1` then fans the
former per-execution chain out over that array, one iteration per element
(`weclapp-articles-to-as.yaml` has no `ForEach@1` - it renders one batch per tick). The ck and ai
loops have ONE child each, the `If@1` system-record gate that wraps the whole body (`If@1` always
continues with the next node, so a step left outside it would run for loading equipment or the
anonymous debitor); the ai loop is the exception on one further count: the FIRST child of its gate
is a per-order `MakeHttpRequest@1` customer lookup. The ai, ar and be loops carry
`continueOnError: true`, so a customer or a file that fails permanently fails its own iteration
instead of starving the tick. The as pipeline
starts with its export-run key instead of
a fetch - `SetPrimitiveValue@1` names the kind, then one `DateTime@1` `Now`, one
`ConvertToTimeZone` to `Europe/Vienna` and two `Format` steps write
`{ exportKind, exportDay, fileName }` (the name assembled by `FormatString@1`), and BOTH its
fetches sit inside the K1 gate, so an already-delivered day costs no WeClapp request at all. The
delivery file name comes out of the SAME clock read as the marker day: two reads can straddle
Vienna midnight, and the file would then carry day N+1 under the marker of day N - with no marker
for N+1, the next tick delivers that day a second time. `DateTime@1` reads `DateTime.UtcNow` and
cannot be handed a clock from a test, so that coupling is pinned STRUCTURALLY in
`AsExportGateTests`: exactly one `Now` node, and both `Format` nodes read the instant the single
`ConvertToTimeZone` produced - a second read would appear as a second node and fail there.
`PipelineChainIntegrationTests` seeds that instant and pins the resulting day and name.

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
(`ApplyChanges@2`, `SftpUpload@1`, `WeClappArWrite@1`, ...).

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
`ForEach@1`'s `keyPath` to `$.current`;
`ArBeYamls_FetchTheirFilesThroughSftpListAndSftpDownload` pins the AR/BE return-path wiring
(`SftpList@1` -> the per-file `ForEach@1` over the same path -> `SftpDownload@1` as the FIRST child
of the marker gate, reading `$.current.fullPath` from the same server entry; the write node's
`contentPath` is the download's `targetPath` and its `fileNamePath` is `$.current.name`) — every
one of those strings can be changed on ONE side and still ship green, doing the wrong amount of
work — plus each yaml's `filePattern` EXACTLY (`AR*TXT` / `BE*txt`) and `minFileAgeSeconds >= 60`;
`ArBeYamls_ConfigureTheProcessingModeExactlyOnce` allows the processing mode in exactly one place
per ar/be yaml, one `SetPrimitiveValue@1` on `$.mode` before the loop holding `dryRun` or `live`;
`ArBeYamls_DryRunWriteNode_ForbidsTheLiveMode` couples that mode to the write node's `dryRun` in
BOTH directions (`live` with `dryRun: true` would consume the only copy of an LKV file without
writing it, `dryRun` with `dryRun: false` would book a file a second time after the flip), reading
both values through the same binding the tenant uses rather than off the raw text — YAML has
several spellings of true (`yes`, `on`, `!!bool true`) and a text probe written for `true` passes
them all as "not set";
`ArBeYamls_SftpDelete_IsGatedOnTheLiveModeBehindTheMarker` pins `SftpDelete@1` as the only child of
an `If@1` on `$.mode == live` that is the LAST per-file `ForEach@1` child, AFTER the marker gate
whose last child is `ApplyChanges@2`, with `onMissingFile: Ignore` (the product default is Fail);
`ArBeYamls_ForEachIsolatesAFailingFile` pins `continueOnError: true` on the per-file loop, the only
isolation left on the return path since the retired gate took its try/catch with it;
`ArBeYamls_InboundFileMarker_KeysOnTheListingElementAndTheMode` pins the marker key
(`FormatString@1` over the listing element's server, directory, name, length, verbatim
`lastWriteTimeUtc` text and the mode), the probe (`GetOrCreateRtEntitiesByType@1` on
`Industry.Logistics/InboundFile`, one `FileKey Equals` filter on that key), the gate on the probe's
mod operation (`Equal 0` = Insert) and the seven typed attribute updates the marker writes, in the
order download -> write -> `DateTime@1 Now` -> `CreateUpdateInfo@1` -> `ApplyChanges@2`;
`ArBeChainTests` runs that loop body with the real nodes against fakes (the ar yaml throughout, the
be yaml once with its own write node) and pins that the marker is handed to the repository BEFORE
the file is deleted, that only the live mode deletes, that a known file is neither read nor written
again and that a repository or write failure leaves the file on the server;
`ArBeYamls_ReadDilosFilesAsIso88591` pins the effective `encoding` of every `SftpDownload@1` to
the code page the DILOS parsers expect (the node defaults to utf-8, which turns Latin-1 umlauts
into replacement characters without failing anything);
`AllPipelineYamls_UseApiConfigurationOnly_NoInlineCredentialsOrPlaceholders` keeps WeClapp access
on `apiConfiguration` (no inline `apiKey`/`baseUrl`, no substitution placeholder);
`AllPipelineYamls_EveryAttributeUpdate_DeclaresValueType` requires every `ApplyChanges` attribute
update to declare its `valueType`; `ArticlesToCkYaml_ConfiguredPaths_ResolveAgainstTransformOutput`
and `OrdersToAiYaml_ConfiguredCkPaths_ResolveAgainstOrderTransformOutput` resolve every configured
value path against the `$.ck` view the SHIPPED mapping chain builds (ck FLAT, ai NESTED under
`Customer`/`Order`), so a path that silently resolves to null cannot ship; `CkViewParityTests` pins
that view against the fixtures frozen from the former `WeClappToCk@1`;
`ArticlesToCkYaml_SystemArticleGate_WrapsThePersistence` and
`OrdersToAiYaml_SystemOrderGate_IsTheFirstLoopChildAndWrapsTheLookup` pin the system-record filters
as `If@1` gates that wrap the WHOLE loop body - `If@1` always continues with the next node, so a
step left outside the gate would run for loading equipment or the anonymous debitor - and pin each
gate's literal to the core predicate (`WeClappToDilos.IsSystemArticle`, which the AS delivery
still calls, and `IsSystemCustomer`), so the ck gate and the AS delivery cannot disagree on what a
system article is;
`ArticlesToCkYaml_AbsentOptionalFields_KeepTheParitySeeds` pins the `""`/null seeds an absent name
or EAN keeps (`CreateUpdateInfo@1` clears an attribute on null and skips it on absent, so the seed
is what makes a removed EAN clear on the nightly update; a name or shop number that is JSON null
keeps its `""` seed as well, because `SelectByPath@1` skips a null source, where the former node
wrote null - no WeClapp sample carries such a null); `ArticlesToCkYaml_SystemArticle_LeavesNoView`
and `OrdersToAiYaml_SystemOrder_LeavesNoView` pin that a system record leaves no `$.ck` at all;
`OrdersToAiYaml_CustomerNumberCheck_PrecedesTheNameChain` pins the loud existence check
(`SetPrimitiveValue@1` throws on an absent `valuePath`) BEFORE the name chain, because
`TransformString@1` ends the chain silently when its path matches nothing, plus the `""` seed
before `SelectByPath@1` and the Int64 literals the date guard needs to compare two numbers;
`OrdersToAiYaml_MissingCustomer_FailsTheOrder` pins that an empty customer lookup fails the order
with the path in the message; `OrdersToAiYaml_NonNumericQuantity_FailsInTheRender` pins that the
quantity is validated by the DILOS writer inside the delivery gate, before the upload and the
marker; `OrdersToAiYaml_OrderDate_ReadsEpochAsInt64` pins that a real epoch converts and that 0 or
an absent date leaves no `OrderDate`; `OrdersToAiYaml_CustomerNameUpdate_ResolvesForB2cCustomers`
covers the B2C case below (empty, absent, null and non-empty company);
`AsAiYamls_DeliverViaSftpUploadInIso88591` pins every shipped `SftpUpload@1`: effective
`encoding` `iso-8859-1` resolving to the same code page the render side writes, effective
`onEncodingError` `Replace`, and the delivered name coming from `fileNamePath` rather than a
static `fileName` or a binary `fileRtId` source. All four are read from the BOUND configuration,
so a property left out of the yaml passes on its default — that is deliberate for
`onEncodingError` (default `Replace`) and caught for `encoding` (default utf-8, which would ship
mojibake to LKV without failing anything). The name pin exists because the retired custom node
had no static-name property at all: the swap widened that surface, and a static name would make
every delivery overwrite the previous one;
`AsAiYamls_SftpUpload_ReadsTheRenderOutputAndTargetsTheLkvRoot` pins what contract 13 leaves
open: that a delivery has exactly ONE content source (ai `DilosRender@1` OR as
`RenderDelimitedText@1`, never both and never neither), that `SftpUpload@1` reads exactly what
that source wrote (`path` == `targetPath`), that its `fileNamePath` matches whichever node names
that delivery (the ai render's `fileNameTargetPath`, the as `FormatString@1`'s `targetPath`) and
that only ONE node writes the name, that it delivers to the SFTP root, and that
it names the same tenant SFTP entry the AR/BE return path uses - every one of those strings can be
renamed on ONE side, ship green and surface on staging at the earliest;
`AsYaml_RenderDelimitedText_SpellsOutTheThirtyFourColumnDilosLayout` pins the AS file format
itself, which since the swap lives in the yaml and nowhere else: 34 columns in order, the eight
populated positions on their exact DILOS field numbers, every other column empty, `required` on
field 3 alone, and the effective `delimiter`/`lineEnding`/`trailingNewLine`/`onDelimiterInValue` -
read as the node reads them, since those options are nullable and resolve their defaults at the
read site, so an omitted property must be checked as its EFFECTIVE value;
`AsYaml_EmptyRenderOutput_IsGatedBeforeTheDeliveryAndTheMarker` pins the empty-batch brake: an
`If@1` whose `path` is EXACTLY the render's `targetPath`, `operator: NotEqual`, `valueType:
String`, `value: ""`, with the upload, the marker and `ApplyChanges@2` all inside it. Both halves
matter - `If@1` reads a missing path as null and null != "" is TRUE, so a mistyped path OPENS the
gate, and a step left outside it would run on an empty batch; `AllPipelineYamls_ApplyChanges_IsVersion2`
forbids the deprecated `ApplyChanges@1` — its configuration is a bare record with no association
property at all, so adding an `associationUpdatesPath` there does NOT drop associations quietly:
the strict deserializer rejects the unknown property and the pipeline registration fails at the
tenant. The guard moves that failure earlier, into this suite and next to the edit.
`SourceYamls_EveryMakeHttpRequest_FailsLoudlyOnHttpErrors` pins `onHttpError: Throw` on every
`MakeHttpRequest@1` of the three source pipelines - the node's default is `LogAndStop`, which logs,
skips the rest of the chain and finishes the execution GREEN, so a WeClapp outage would become a
silent no-delivery on alerting built around failed executions;
`SourceYamls_PagedMakeHttpRequest_ReadsTheWeclappResultArray` pins every paged request's
`itemsPath` to `$.result`, the envelope every WeClapp entity response wraps its elements in;
`OrdersToAiYaml_PagedOrderRequest_FiltersOnConfirmedOrders` pins
`status-eq=ORDER_CONFIRMATION_PRINTED` in that pipeline's order url (selected by `/salesOrder`,
since the pipeline pages a second WeClapp entity now) - the customer's historical order stock is
CLOSED and the dedup gate stops only REPEAT deliveries, so a url edit that drops the filter would
mass-deliver the whole backlog on the next tick with nothing failing. It also pins that the
per-order customer lookup is NOT paged, which the retired "the single paged request" predicate used
to say implicitly: paging replaces the response body at `targetPath` with the flattened item array,
so `$.customerResponse.result[0]` would resolve to nothing and every AI file would ship without a
recipient;
`OrdersToAiYaml_TaxLookupFeedsTheAiRender` pins the join behind the position MwSt rate: exactly one
`/tax` request, PAGED (235 tax entities against a page size of 100), at the top level and BEFORE the
per-order `ForEach@1` (an index comparison - placed AFTER the loop a fetch is still "outside" it and
every order would fail on a rate that is not in the context yet), and `DilosRender@1`'s `taxesPath`
equal to that request's `targetPath`. Each of those can be edited alone and ship green, and the
failure is the quiet kind - an empty MwSt field is the legitimate value for a position that states
no tax, and the partner's own files carry it, so a file missing the promised rate looks exactly like
a correct one;
`OrdersToAiYaml_CustomerLookupFeedsTheOrderTransform` pins the three things that must agree for
an AI file to carry a recipient (the lookup's `targetPath`, the paths the existence check and the
name chain read, and the lookup being the FIRST child of the system-order gate), plus the lookup
addressing THIS order's `customerId`;
`OrdersToAiYaml_ForEachIsolatesAFailingOrder` pins `continueOnError: true` on the per-order loop,
so one permanently failing customer fails its own order instead of starving the whole tick;
`SourceYamls_AreCoveredByTheApiConfigurationGuard` pins the five pipelines that must carry the
`WeClappApi` entry by NAME, because the guard above keys on node type names and a file that drops
out of that trigger stops being checked without failing.

This list is machine-checked: `DocumentationContractTests.ClaudeMd_NamesEveryPipelineContractTest`
fails the suite if a `PipelineYamlContractTests` guard is not named here. It drifted once (AB#4845
added two guards without documenting them, leaving 5 of 12 listed) — an inventory that falsely
claims completeness invites re-pinning an invariant that already holds.

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
  source: the orders→AI yaml builds `$.ck.Customer.Name` from the CUSTOMER record itself
  (`Concat@1` + `TransformString@1 Trim` for the person, overridden by a non-empty company through
  `If@1 RegexMatch .+`) — the Customer update must keep reading `$.ck.Customer.Name`, because a
  path aimed at the raw company field leaves the CK name empty for B2C (live finding 2026-07-16);
  a company that is JSON null keeps the person name (the former node threw there). `DilosOrderWriter`
  (`RecipientName1`/`RecipientName2`) builds the DILOS FILE name fields from the ADDRESS instead;
  name2 ("Nachname Vorname") stays empty unless a company fills name1.
- **The AI position prices are not read off the golden files.** Those carry fields 18 and 20 and
  leave 16, 19 and 21 empty, which is what the previous shop connector happened to produce (and it
  wrote the same number into 18 and 20). The contract is that a position states its rate and all
  four prices: 16 MwSt in WHOLE PERCENT, 18/20 the unit price net/gross, 19/21 the line price
  net/gross. Field 16 is deliberately NOT the DILOS tax key — the partner's key table maps 20 % to
  key 6 AND to key 20, so a key is ambiguous where the rate never is (spec: "Zahl Integer … MwSt.
  in Prozent, ohne Kommastelle"; a rate WeClapp carries with decimals is rounded to whole percent).
- **WeClapp states LINE totals, not unit prices.** A position's `netAmount`/`grossAmount` are the
  line amounts; its `unitPrice` is the pre-discount LIST price and matches neither (live sample:
  unitPrice 48.03, 7 % discount, gross 44.67, net 37.23). So 19/21 carry the API values verbatim
  and 18/20 divide them by the quantity — the rule field 18 always followed. Consequence to keep in
  mind: at a quantity that does not divide evenly, `18 × Menge` differs from 19 in the last cent
  (10.00/3 → 3.33 × 3 = 9.99). The line total is the invoiced amount and stays authoritative; the
  alternative (deriving 19 from the rounded 18) would make the file self-consistent but disagree
  with WeClapp and with K* field 65.
- **The rate needs a second entity, and it is validated LATE.** A position names a `taxId`, never a
  percentage — the rate lives on the WeClapp `tax` entity (`taxValue`, a STRING, e.g. "20"/"13.5").
  The ai yaml fetches `/tax` once per tick into `$.taxes`, the same fetch-the-second-entity shape
  the AS delivery uses for `articleSupplySource`. `DilosRender@1` only INDEXES that set (array
  present, an id per entity, no id twice — the last doubling as the paging-overlap detector) and
  carries every `taxValue` across unparsed; `DilosOrderWriter` parses and range-checks the rate at
  the position that names it. That split is deliberate: parsing at index time would validate all
  235 entities on every execution, so one broken record none of today's orders is taxed under would
  fail every order until someone repaired it. A position naming a tax entity that was not fetched,
  or one whose rate is unreadable or outside 0-100, fails THAT order rather than rendering an empty
  rate: empty is legitimate for a position that states no tax at all, so the two are
  indistinguishable in the delivered file — and the AI delivery writes its export marker on the way
  out, which would make the wrong file the final one for that order.
- **Amounts and rates parse with `AllowDecimalPoint | AllowLeadingSign`, never `NumberStyles.Any`.**
  Under `Any` + InvariantCulture a comma is a GROUP separator and parentheses are an accounting
  negative, so `"44,67"` reads as 4467 and `"(20)"` as -20 — silently, straight into a delivered
  file. A missing, empty or unreadable value fails the order instead of standing in as `0`: that
  fallback predates the price agreement, and now that the prices are contract it is an actively
  wrong statement nothing downstream can tell from a genuine zero. A real zero still renders `0.00`.
  **The quantity obeys the same rule — it is not the exception it looks like.** Field 11 carries it
  as text, but 18 and 20 are the line amounts DIVIDED by it, so an unreadable quantity used to make
  `PerUnit` state the LINE amount as the unit price: threefold on a real quantity of 3, while 19 and
  21 beside it stayed correct, so no sum inside the file disagrees and nothing downstream can see
  it. `WeClappOrderItem.Quantity` defaults to `""`, which makes that path reachable rather than
  theoretical. A parseable `0` keeps its documented meaning: nothing to divide by, so the unit price
  is the line amount.
- **A guard that runs later than another cannot be tested with a fixture the earlier one rejects.**
  `DilosRenderNode` renders the CONTENT before it builds the file name, so the four file-name guard
  tests went vacuous the moment amounts became fail-loud: their orders carried no `GrossAmount`, the
  render refused first, and asserting only the exception TYPE kept them green while proving nothing
  (verified — with BOTH name guards deleted, all 39 tests of the class still passed). Fixtures for a
  late guard must render, and every one of those tests asserts the guard's own MESSAGE now.

## AS/AI Delivery (WeClapp → SFTP)
- Render and transport are separate nodes, and the RENDER differs per delivery kind: AI content
  comes from `DilosRender@1`, AS content from the product's `RenderDelimitedText@1` (see "The AS
  file format lives in the yaml" below). The file name comes from `DilosRender@1` for AI (per
  order) and from `FormatString@1` for AS (per Vienna day,
  same clock read as the marker), and `SftpUpload@1` (`encoding: iso-8859-1`,
  `onEncodingError: Replace`) writes both to the LKV SFTP root. The tenant entry (`LkvSftp`) MUST carry a `MaxConcurrentConnections` value
  (3) — the CK attribute is optional but the node reads a non-nullable int, and an unset value
  kills every run while the entry is deserialized (staging, 2026-08-21).
- The two content guards have different homes now, because nothing downstream repeats them:
  `SftpUpload@1` uploads empty content as a 0-byte file, and resolves a file name carrying path
  segments to its last segment instead of refusing it. AS: the empty-batch brake is the `If@1`
  in the yaml (a batch of nothing but system articles is legitimate). The AS name has NO guard any
  more (AB#5096): it is assembled in the yaml from a literal export kind and a generated 14-digit
  stamp, so a path separator is unrepresentable. AI: `DilosRender@1` still throws on empty content,
  which is always an upstream defect there, and on any name containing `/`, `\` or `..` - the AI
  name carries the external WeClapp order number, so the guard stays where the risk is. That rule
  lives in `DilosFile.IsPlainFileName`.
- **The AS file format lives in the yaml.** `RenderDelimitedText@1` renders the 34 columns
  spelled out there; the two things a column model cannot do stay adapter-side in
  `WeClappResolveSupplySources@1`, which drops system articles (`LOADING_EQUIPMENT`) and projects
  the EK-Preis as a finished scalar on `ekPreis` (first parseable
  `supplySources[].articlePrices[].price`, absent means `0`, format `0.####` invariant).
  **The drop runs BEFORE the join**, and the order is load-bearing: a system article appears in
  no delivered file, so nothing about it can make one wrong, while joining first let one
  unresolvable stub on a pallet (WeClapp leaves the stub behind when an `articleSupplySource` is
  archived) block every hourly delivery — no file, no marker — over a record that is discarded
  one step later.
  **For the articles that remain, the join fails LOUD**: an `articleSupplySource` without an
  `id`, a stub pointing at an entity that was not fetched, a `supplySources` value that is not
  an array — each one throws naming the element, because all of them end in the same place,
  `ekPreis` = `0`, which is
  also the legitimate value for an article without a purchase price. Neither the delivered
  file nor any downstream step can tell those apart, and the delivery burns the per-day
  marker on its way out, so a silently mispriced article master would stand at LKV for the
  whole Vienna day (recoverable only by deleting the CK marker). A throw costs the next tick
  and no data — the same trade both fetches (`onHttpError: Throw`) and the render
  (`onDelimiterInValue: Fail`) already make. Read-only census of the customer account
  (2026-08-28): 48 articles, 16 `articleSupplySource` entities, 15 stubs, **0 dangling** — the
  loud path is not reachable from today's live data. An explicit `"supplySources": null` is
  NOT part of that: it means what an absent property means (no price) and is normalised. The byte
  anchor is `AsDeliveryParityTests`: it drives the SHIPPED yaml's own node configurations over a
  fixture batch and compares the result byte-for-byte, in ISO-8859-1, against
  `tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt` - the frozen output of the
  pre-swap renderer, re-frozen on the record separator below (one CR per LF, payload untouched).
  That fixture carries a `.gitattributes` `-text` entry: a checkout never strips its CRs and nor
  does a plain re-add, but `git add --renormalize` does, and that is the sweep a repo-wide `text`
  rule arrives with.
- **The two deliveries separate their records differently, on purpose.** AS is CR+LF
  (`lineEnding: CrLf` on the render node - the node's own default is LF, so this is one of the
  properties the yaml must not leave out): that is what `_specs/AS.md` asks for and what the
  partner fixed for the article master. AI stays LF, which the partner's own files show. The AS
  separator is pinned in six places that move together, because every one of them reads green
  against the wrong separator on its own; the yaml lists them next to `lineEnding`, and that
  comment is the single copy of the list.
- **A dry run proves less than it used to**: `SftpUpload@1` returns at its dry-run gate BEFORE
  it resolves the content path and encodes it (the retired custom node deliberately did both
  first). A dry-run probe therefore proves only that the `LkvSftp` entry resolves and carries
  auth material and that the file name is there — never that a connection works, that `path:`
  points at real content, or that the encoding survives. All three surface on the first real tick.
- **A successful upload leaves no log line**: `SftpUpload@1` logs nothing on success, and its
  encoding warning names neither the file nor the order (at most 20 distinct code points).
  Delivery proof is the SFTP listing plus the CK export marker, never the log — inside the
  per-order AI `ForEach@1` nothing says WHICH order was degraded. Product follow-up (success
  info + file attribution) is tracked for C2.

## AR/BE Return Path (SFTP → WeClapp)
- The chain is `SftpList@1` → `SetPrimitiveValue@1` (`$.mode`) → `ForEach@1` [ `FormatString@1`
  (marker key) → `GetOrCreateRtEntitiesByType@1` (marker probe) → `If@1` on Insert [
  `SftpDownload@1` → `WeClappArWrite@1`/`WeClappBeWrite@1` → `DateTime@1 Now` →
  `CreateUpdateInfo@1` → `ApplyChanges@2` ] → `If@1` on `$.mode == live` [ `SftpDelete@1` ] ]. The
  product nodes own the SFTP mechanics (credentials via tenant GlobalConfiguration entry `LkvSftp`,
  same JSON shape as `SftpUpload@1`), this adapter owns the DILOS policy. `SftpList@1` seeds
  `$.files` with metadata for every matching, ready file — always the array, even `[]` — and
  `minFileAgeSeconds: 60` keeps a file that is still being written out of the listing (the node
  defaults to 0). `SftpDownload@1` sits INSIDE the marker gate, one file per iteration, and needs
  `encoding: iso-8859-1`: its default is utf-8, and DILOS files are Latin-1.
- The per-file state is an `Industry.Logistics/InboundFile` marker (CK 2.1.0, one instance per
  processed file and mode, unique index on `FileKey`), not memory: it survives pod restarts and
  redeploys, and it is visible in the Studio. The key is
  `{serverConfiguration}|{remoteDirectory}|{name}|{length}|{lastWriteTimeUtc}|{mode}`, built by
  `FormatString@1` from the element `SftpList@1` emitted, with the listing's own `lastWriteTimeUtc`
  TEXT carried through verbatim (`FormatString@1` reads a JSON string as a string; re-parsing and
  re-formatting it would tie the identity to the reader's format choice, and an unchanged file
  would key differently from one tick to the next). A `|` inside a DILOS file name is not
  guarded against - `SftpList@1` drops names with path separators, and the names are `AR*.TXT` /
  `BE_*.txt`.
- `$.mode` lives on ONE `SetPrimitiveValue@1` before the loop and NOWHERE else: `dryRun` (validate
  only, dryRun marker, no delete) or `live` (real write, live marker, delete). It is part of the
  key on purpose - a file validated in `dryRun` is not the same processing as the live one, so the
  backlog on the LKV server runs once for real after the flip to `live`, exactly the go-live rule
  the retired in-memory keep mark implemented. The write node's `dryRun` is a second place; the
  contract test couples the two. The DEFAULT in the repo is the safe side (`dryRun` + `dryRun:
  true`): flip `mode: live` together with `dryRun: false` for go-live.
- The marker is persisted by `ApplyChanges@2` as the LAST child of the Insert gate, only after the
  write succeeded, and the delete runs in its own gate AFTER it: a crash between the two leaves a
  marked file on the server, which the next tick only deletes (the probe answers Update, the gate
  stays closed). A download, write or marker step that THROWS aborts the iteration before the
  delete (the one non-throwing failure is the residue below); `continueOnError: true` isolates
  that file and fails the run at the end. The probe is a query, not an atomic claim, so two
  executions of the same pipeline that OVERLAP could both see Insert for one file before either
  persists its marker. A second replica is not that case - the cron trigger is a competing
  consumer on one named queue per tenant and pipeline (`FromPipelineTriggerEventNode` +
  `EventHubControl.RegisterRoutedEventConsumer(address, ...)`), so a tick runs on ONE pod - but a
  manual run beside a tick, or a tick that outlasts its interval, is. Then both write (the
  writes are idempotent by design) and the unique `FileKey` index rejects the second marker. Keep
  the chart's `replicaCount: 1` (its default) all the same - the constraint the AS export marker
  already carries. `SftpDelete@1` honours
  the execution mode only, which a cron tick never carries (the mesh adapter's
  `FromPipelineTriggerEvent@1` starts every tick without one), so the `$.mode` gate is the only
  thing between a validation tick and a consumed LKV file. `onMissingFile: Ignore` is explicit
  because the product default is Fail, and it is a judgment call rather than a necessity: every
  file the delete is asked for was listed seconds earlier, so only someone else removing it inside
  the tick reaches this, and a run whose marker is already persisted must not go red for a file
  that is gone anyway (the node applies Ignore to a missing file only; a permission error still
  fails).
- Accepted residue: `ApplyChanges@2` logs and continues when the repository reports errors through
  its OperationResult instead of throwing, so a WRITTEN file could be deleted without a marker (no
  data lost, no trace). The AS marker carries the same residue; a product change is noted for the
  alerting stage.
  And the markers are never pruned: about 20 per working day at the customer's pace, kept as
  history until the operations stage decides on a retention. Because they are never pruned, a
  file re-delivered byte-identical WITH its modification time preserved (an archived copy
  re-uploaded by a tool that keeps mtime) keys the same as the processed one and is only deleted -
  the retired confirm node forgot a key after a successful delete, this design does not. Delete
  the marker instance before such a re-upload; a re-upload that changes the mtime is a new file.
- A dry run started through `FromExecutePipelineCommand@1` (mesh adapter >= the AB#5159 fix)
  reaches every node with `IsDryRun`: the write nodes validate, `ApplyChanges@2` and
  `SftpDelete@1` record their intent and touch nothing - the way to validate a `live` definition
  without consuming a file. That fix (octo-mesh-adapter `ae68ec0`) is in NO published SDK up to
  3.4.112: on an older image the command starts a NORMAL execution, and a `mode: live` +
  `dryRun: false` definition then writes and deletes for real. Validate a live definition this way
  only on an image that carries the fix.
- Two ways the wiring can be wrong without anything failing, both guarded because both are one
  Studio edit away: a probe without `fieldFilters` (`GetOrCreateRtEntitiesByType@1` logs an error
  and STOPS the chain - every tick green, no file processed) and a `SftpDownload@1` or
  `SftpDelete@1` naming a different `serverConfiguration` than the listing (content from one
  server, deletion on the other). Both are pinned by the shipped-yaml contracts.
- Importing a pipeline YAML that uses a config key the DEPLOYED image does not know yet fails the
  pipeline registration (the SDK YAML deserializer rejects unknown properties) → deploy the new
  image before importing updated YAMLs. The SFTP swap that put the product's `SftpList@1` and
  `SftpDownload@1` in front of the adapter's return-path nodes changed both directions at once and
  counts as the first two cases of that rule; the next three follow.
- **The AS swap is a THIRD case of the same rule, and it fails LATER than the other two.**
  `RenderDelimitedText@1` ships in SDK **3.4.101 and in no earlier version** (verified across the
  whole local package cache and the hand-maintained `999.0.0` DebugL feed), so importing the new
  `weclapp-articles-to-as.yaml` against an older image fails registration outright. The reverse
  order fails at RUN time instead: the stored definition still names `DilosRender@1` with
  `mode: AS`, every property on `DilosRenderNodeConfiguration` still exists, registration
  succeeds silently, and the next tick throws "Unknown DilosRender mode 'AS'" — no delivery, no
  marker, hourly, until someone re-imports the as yaml. Both directions leave a window without an
  AS delivery: deploy the image FIRST, then re-import **every** changed yaml including the as one.
- **The CK-view swap (AB#5162) is a FOURTH case, and it INVERTS the order: tenant yamls FIRST,
  image second.** The new image no longer registers `WeClappToCk@1`, so a stored ck or ai
  definition that still names it is rejected at load time (unknown discriminator) and BOTH
  pipelines stay unregistered until they are re-imported - no CK article sync, no AI delivery.
  Importing the two yamls first is windowless: every node, operator and property they use exists
  in SDK 3.4.109 (commit 071725d, the SDK the staging image carried before the swap; the test runs
  pinned at `-p:OctoVersion=3.4.109` prove the yamls deserialize there) and later, and the old
  image keeps the then-unused node registered. Staging: `DeployPipeline` ck + ai from the branch,
  then merge, train and lift; prod-2 gets yamls and image together in its one lift.
- **The file-state swap (AB#5181) is a FIFTH case of the rollout rule, and like the CK-view swap it
  INVERTS the order: tenant yamls FIRST, image second.** The new image no longer registers
  `DilosFileGate@1`/`DilosFileConfirm@1`, so a stored ar or be definition that still names them is
  rejected at load time (unknown discriminator). Importing the two yamls first is windowless on an
  image at SDK 3.4.112 or later: `SftpDelete@1` ships in 3.4.112 and in no earlier version (the
  staging chart 3.4.113 carries it), the rest since 3.4.109. On an older image the yaml import fails
  registration (unknown discriminator `SftpDelete@1`) and the image-first order fails the same way
  at load time - lift that image to >= 3.4.112 before either step. `Industry.Logistics` 2.1.0 has
  to be imported BEFORE the yamls (the probe fails loud on a tenant without the type). Staging:
  import the CK, `DeployPipeline` ar + be from the branch, then merge, train and lift; prod-2 gets
  CK, yamls and image together in its one lift.
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
