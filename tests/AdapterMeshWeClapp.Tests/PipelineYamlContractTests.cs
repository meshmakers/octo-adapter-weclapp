using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lkv.WeClapp.Core.Dilos;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Contract tests over the SHIPPED pipeline YAMLs — the gap that let three config bugs
/// reach the first tenant run (2026-07-16, staging): CreateUpdateInfo@1 drops any
/// attribute update without an attributeValueType (error is debug-only, the execution
/// still completes), and value paths that do not match the WeClappToCk output shape
/// resolve to null (GetOrCreate then matches on null and would duplicate on every run).
/// These tests parse the real files with the platform's own strict serializer and check
/// both contracts against the real transform node output.
/// </summary>
public class PipelineYamlContractTests
{
    // Enumerated from the repo so a future pipeline yaml cannot silently escape the
    // guard (the strict deserializer fails loudly on unregistered node types instead).
    // BOTH extensions: a pipeline saved as .yml must not slip past every contract here —
    // the enumeration feeds them all, including the theory-lockstep meta-test.
    private static string[] AllPipelineYamls
    {
        get
        {
            var pipelinesDir = Path.GetDirectoryName(FindRepoFile(Path.Combine("pipelines",
                "weclapp-articles-to-ck.yaml")))!;
            return Directory.GetFiles(pipelinesDir, "*.yaml")
                .Concat(Directory.GetFiles(pipelinesDir, "*.yml"))
                .Select(Path.GetFileName)
                .Cast<string>()
                .ToArray();
        }
    }

    // ---------- contract 1: every attribute update declares its value type ----------

    [Fact]
    public async Task AllPipelineYamls_EveryAttributeUpdate_DeclaresValueType()
    {
        Assert.True(AllPipelineYamls.Length >= 5,
            "pipeline yaml enumeration must find the shipped pipelines");

        var violations = new List<string>();

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            foreach (var config in Walk(root.Transformations).OfType<CreateUpdateInfoNodeConfiguration>())
            {
                foreach (var update in config.AttributeUpdates ?? [])
                {
                    if (update.AttributeValueType == null)
                    {
                        violations.Add($"{yaml}: CreateUpdateInfo '{config.Description}' " +
                                       $"update '{update.AttributeName}' has no attributeValueType");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    // ---------- contract 2: the ck yaml's paths resolve against the real transform output ----------

    [Fact]
    public async Task ArticlesToCkYaml_ConfiguredPaths_ResolveAgainstTransformOutput()
    {
        var root = await DeserializePipeline("weclapp-articles-to-ck.yaml");
        var all = Walk(root.Transformations).ToList();
        var toCk = Assert.Single(all.OfType<WeClappToCkNodeConfiguration>());
        var lookup = Assert.Single(all.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
        var updateInfo = Assert.Single(all.OfType<CreateUpdateInfoNodeConfiguration>());

        var dataContext = await RunToCkNode(toCk, """
            {"current":{"id":"168914","articleNumber":"TW_Z_074","name":"Ersatz Schnellverschlüsse",
             "articleType":"STORABLE","ean":"9001234567890","active":true}}
            """);

        foreach (var filter in lookup.FieldFilters ?? [])
        {
            var path = Assert.IsType<string>(filter.ComparisonValuePath);
            Assert.False(string.IsNullOrEmpty(dataContext.Get<string?>(path)),
                $"lookup filter path '{path}' resolves to nothing — " +
                "GetOrCreate would match on null and create duplicates on every poll");
        }

        foreach (var update in updateInfo.AttributeUpdates ?? [])
        {
            var path = Assert.IsType<string>(update.ValuePath);
            Assert.False(string.IsNullOrEmpty(dataContext.Get<string?>(path)),
                $"attribute update '{update.AttributeName}' path '{path}' resolves to nothing");
        }
    }

    // ---------- contract 3: the ai yaml's customer name survives B2C orders (no company) ----------

    [Fact]
    public async Task OrdersToAiYaml_CustomerNameUpdate_ResolvesForB2cCustomers()
    {
        var root = await DeserializePipeline("weclapp-orders-to-ai.yaml");
        var all = Walk(root.Transformations).ToList();
        var toCk = Assert.Single(all.OfType<WeClappToCkNodeConfiguration>());
        var gate = Assert.Single(all.OfType<IfNodeConfiguration>());
        var customerUpdate = (gate.Transformations ?? [])
            .OfType<CreateUpdateInfoNodeConfiguration>()
            .Single(c => c.CkTypeId == "Industry.Logistics/Customer");
        var nameUpdate = (customerUpdate.AttributeUpdates ?? []).Single(u => u.AttributeName == "Name");

        // B2C: private customer without a company — exactly the case Jürgen reported
        // as an empty recipient name on 2026-07-16.
        var dataContext = await RunToCkNode(toCk, """
            {"current":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customerResponse":{"result":[
              {"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}]}}
            """);

        var namePath = Assert.IsType<string>(nameUpdate.ValuePath);
        var resolved = dataContext.Get<string?>(namePath);
        Assert.False(string.IsNullOrWhiteSpace(resolved),
            $"customer Name path '{nameUpdate.ValuePath}' is empty for a B2C order — " +
            "CkCustomer.Name carries the person fallback and must be the source");
        Assert.Equal("Erika Muster", resolved);
    }

    // ---------- contract 4: ALL ai yaml $.ck paths resolve against the Order-mode output ----------

    [Fact]
    public async Task OrdersToAiYaml_ConfiguredCkPaths_ResolveAgainstOrderTransformOutput()
    {
        // Article mode writes the CK document FLAT at $.ck, Order mode writes a NESTED
        // CkOrderDocument — the exact confusion that broke the ck yaml. A symmetric
        // "flattening" of the ai yaml would break the dedup-gate probe (filter resolves
        // to null → every order re-delivered per poll + duplicated CK entities), so every
        // $.ck path in the file is pinned here against the real transform output.
        var root = await DeserializePipeline("weclapp-orders-to-ai.yaml");
        var all = Walk(root.Transformations).ToList();
        var toCk = Assert.Single(all.OfType<WeClappToCkNodeConfiguration>());

        var dataContext = await RunToCkNode(toCk, """
            {"current":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customerResponse":{"result":[
              {"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}]}}
            """);

        var ckPaths = new List<(string What, string Path)>();
        foreach (var lookup in all.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>())
        {
            foreach (var filter in lookup.FieldFilters ?? [])
            {
                if (filter.ComparisonValuePath is { } path && path.StartsWith("$.ck", StringComparison.Ordinal))
                {
                    ckPaths.Add(($"lookup filter of {lookup.CkTypeId}", path));
                }
            }
        }

        foreach (var update in all.OfType<CreateUpdateInfoNodeConfiguration>())
        {
            foreach (var attributeUpdate in update.AttributeUpdates ?? [])
            {
                if (attributeUpdate.ValuePath is { } path && path.StartsWith("$.ck", StringComparison.Ordinal))
                {
                    ckPaths.Add(($"attribute update '{attributeUpdate.AttributeName}'", path));
                }
            }
        }

        Assert.True(ckPaths.Count >= 7, "the ai yaml must expose its $.ck lookup and update paths");
        foreach (var (what, path) in ckPaths)
        {
            Assert.False(string.IsNullOrEmpty(dataContext.Get<string?>(path)),
                $"{what} path '{path}' resolves to nothing against the real Order-mode output — " +
                "flat-vs-nested drift would re-deliver every order and duplicate CK entities");
        }
    }

    // ---------- contract 5: converted pipeline yamls use passive triggers, no polling fields ----------

    // The expected first-transformation type is parameterized per file: the batch/per-item
    // WeClapp pipelines (as/ck/ai) fetch via WeClappFetchStep@1, the DILOS return-path
    // pipelines (ar/be) fetch via DilosFileFetchStep@1 — each file gets an exact match against
    // its own designated fetch-step type, not a loosened "one of either" check.
    [Theory]
    [InlineData("weclapp-articles-to-as.yaml", typeof(DilosExportRunKeyNodeConfiguration))]
    [InlineData("weclapp-articles-to-ck.yaml", typeof(MakeHttpRequestNodeConfiguration))]
    [InlineData("weclapp-orders-to-ai.yaml", typeof(MakeHttpRequestNodeConfiguration))]
    [InlineData("dilos-ar-to-weclapp.yaml", typeof(SftpListNodeConfiguration))]
    [InlineData("dilos-be-to-weclapp.yaml", typeof(SftpListNodeConfiguration))]
    public async Task ConvertedYaml_UsesPassiveTriggers_NoPollingFields(string file, Type expectedFirstStepType)
    {
        var root = await DeserializePipeline(file);
        Assert.Collection(root.Triggers!,
            t => Assert.IsType<FromPipelineTriggerEventNodeConfiguration>(t),
            t => Assert.IsType<FromExecutePipelineCommandNodeConfiguration>(t));

        var transformations = root.Transformations?.ToList() ?? [];
        Assert.NotEmpty(transformations);
        Assert.IsType(expectedFirstStepType, transformations[0]);

        var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", file)));
        Assert.DoesNotContain("pollingIntervalSeconds", raw);
        Assert.DoesNotContain("runOnStart", raw);
    }

    // The Theory above hard-codes one InlineData row per shipped pipeline yaml — a future 6th
    // yaml dropped into pipelines/ without a matching row would silently escape the
    // passive-trigger ban instead of failing loudly. This proves the InlineData file set and the
    // AllPipelineYamls glob stay in lockstep, the same way AllPipelineYamls itself keeps contracts
    // 1/6 from missing a future file.
    [Fact]
    public void ConvertedYaml_UsesPassiveTriggers_TheoryCoversAllPipelineYamls()
    {
        var theoryMethod = typeof(PipelineYamlContractTests)
            .GetMethod(nameof(ConvertedYaml_UsesPassiveTriggers_NoPollingFields))!;
        var coveredFiles = theoryMethod.GetCustomAttributes<InlineDataAttribute>()
            .SelectMany(attribute => attribute.GetData(theoryMethod))
            .Select(row => (string)row[0]!)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        var allFiles = AllPipelineYamls.OrderBy(file => file, StringComparer.Ordinal).ToList();

        Assert.Equal(allFiles, coveredFiles);
    }

    // ---------- contract 6: every ForEach fan-out carries safe target-path/parallelism params ----------

    // Machine-guards the two ForEach hazards: an omitted (or literal "$") targetPath defaults
    // to "$" and REPLACES the whole document root with the (unordered) loop-merge result; an
    // omitted/non-1 maxDegreeOfParallelism defaults to Environment.ProcessorCount and races the
    // shared cross-tick state / export-dedup markers that every converted pipeline's per-item
    // chain writes through.
    [Fact]
    public async Task AllPipelineYamls_EveryForEach_HasNonRootTargetPathAndSequentialDop()
    {
        var violations = new List<string>();

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            foreach (var forEach in Walk(root.Transformations).OfType<ForEachNodeConfiguration>())
            {
                if (forEach.TargetPath is null or "$")
                {
                    violations.Add($"{yaml}: ForEach '{forEach.Description}' TargetPath is " +
                                    $"'{forEach.TargetPath ?? "null"}' — the default \"$\" REPLACES the document root");
                }

                if (forEach.MaxDegreeOfParallelism != 1)
                {
                    violations.Add($"{yaml}: ForEach '{forEach.Description}' MaxDegreeOfParallelism is " +
                                    $"{forEach.MaxDegreeOfParallelism}, expected 1 — parallel iterations would race " +
                                    "the shared cross-tick state / export-dedup markers");
                }
            }
        }

        Assert.Empty(violations);
    }

    // ---------- contract 7: every ForEach fan-out pins keyPath to $.current ----------

    // DilosFileConfirm@1's Path defaults to "$.current" (the ForEach keyPath convention) and
    // every per-item chain in every converted yaml reads $.current.* — a ForEach configured with
    // a different keyPath would silently break every one of those paths without any structural
    // test noticing (the yaml still deserializes, every node is still "present", it would just
    // read nothing at runtime).
    [Fact]
    public async Task AllPipelineYamls_EveryForEach_KeyPathIsCurrent()
    {
        var violations = new List<string>();

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            foreach (var forEach in Walk(root.Transformations).OfType<ForEachNodeConfiguration>())
            {
                if (forEach.KeyPath != "$.current")
                {
                    violations.Add($"{yaml}: ForEach '{forEach.Description}' KeyPath is " +
                                    $"'{forEach.KeyPath}', expected '$.current' — every per-item child " +
                                    "path (DilosFileConfirm@1's default Path, WeClappToCk's $.current.item, …) " +
                                    "assumes this convention");
                }
            }
        }

        Assert.Empty(violations);
    }

    // ---------- contract 8: the AR/BE return path is listing -> gate -> download ----------

    // The three nodes are only correct together: SftpList@1 emits metadata, DilosFileGate@1 keys
    // the cross-tick state off it and stamps the survivors, SftpDownload@1 reads one file per
    // iteration. A gate pointed at another path gates nothing (and the confirm node then finds no
    // stamp); a download outside the loop would read every already-processed file on every tick.
    // Neither shows up as a failure - the run stays green and does the wrong amount of work - so
    // the wiring is pinned here rather than in a comment.
    [Fact]
    public async Task ArBeYamls_FetchTheirFilesThroughSftpListGateAndSftpDownload()
    {
        var violations = new List<string>();
        var checkedYamls = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            var nodes = Walk(root.Transformations).ToList();
            if (!nodes.OfType<DilosFileConfirmNodeConfiguration>().Any())
            {
                continue; // no DILOS return path in this yaml (as/ck/ai)
            }

            checkedYamls++;
            var list = Assert.Single(nodes.OfType<SftpListNodeConfiguration>());
            var gate = Assert.Single(nodes.OfType<DilosFileGateNodeConfiguration>());
            var download = Assert.Single(nodes.OfType<SftpDownloadNodeConfiguration>());
            var forEach = Assert.Single(nodes.OfType<ForEachNodeConfiguration>());
            var children = forEach.Transformations?.ToList() ?? new List<NodeConfiguration>();

            if (gate.Path != list.TargetPath)
            {
                violations.Add($"{yaml}: DilosFileGate@1 gates '{gate.Path}' but SftpList@1 lists into " +
                               $"'{list.TargetPath}' - the gate would pass judgement on nothing");
            }

            if (forEach.IterationPath != list.TargetPath)
            {
                violations.Add($"{yaml}: the per-file ForEach iterates '{forEach.IterationPath}' but the " +
                               $"listing lands in '{list.TargetPath}'");
            }

            if (children.Count == 0 || children[0] is not SftpDownloadNodeConfiguration)
            {
                violations.Add($"{yaml}: SftpDownload@1 must be the FIRST child of the per-file ForEach - " +
                               "the write node behind it has nothing to write otherwise");
            }

            if (download.RemotePathPath != $"{forEach.KeyPath}.fullPath")
            {
                violations.Add($"{yaml}: SftpDownload@1 reads '{download.RemotePathPath}', expected " +
                               $"'{forEach.KeyPath}.fullPath' - the path of the file this iteration is for");
            }

            // The product node defaults minFileAgeSeconds to 0 and skips the guard entirely at
            // that value, so the yaml literal is the only thing keeping a file that is still
            // being written out of the listing - the same class of quiet failure the encoding
            // pin below covers, where the product default is wrong for DILOS.
            if (list.MinFileAgeSeconds < 60)
            {
                violations.Add($"{yaml}: SftpList@1 lists files younger than " +
                               $"{list.MinFileAgeSeconds}s - a file still being written would be read " +
                               "half finished, and nothing about that run would fail");
            }

            // Without this the two path checks below pass on an empty sequence, and a yaml that
            // lost its write node altogether would read every file and do nothing with it.
            Assert.Single(children.OfType<WeClappWriteNodeConfiguration>());

            var contentPaths = children.OfType<WeClappWriteNodeConfiguration>()
                .Select(w => w.ContentPath).ToList();
            if (contentPaths.Any(path => path != download.TargetPath))
            {
                violations.Add($"{yaml}: the write node reads its content from " +
                               $"'{string.Join(", ", contentPaths)}' while SftpDownload@1 writes to " +
                               $"'{download.TargetPath}'");
            }

            var namePaths = children.OfType<WeClappWriteNodeConfiguration>()
                .Select(w => w.FileNamePath).ToList();
            if (namePaths.Any(path => path != $"{forEach.KeyPath}.name"))
            {
                violations.Add($"{yaml}: the write node takes the file name from " +
                               $"'{string.Join(", ", namePaths)}', expected '{forEach.KeyPath}.name' - the " +
                               "field SftpList@1 emits");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, checkedYamls); // ar + be
    }

    // ---------- contract: the keep/delete mode is configured in exactly ONE place ----------

    // This is what the gate exists for. The mode used to sit on the fetch node AND on the confirm
    // node, and the two had to agree: flipped on the confirm side alone, files were deleted
    // although nothing had been written; flipped on the fetch side alone, every file was
    // reprocessed forever. Both values live in tenant-side pipeline definitions and are editable
    // in the Studio. Raw text on purpose - it catches a second occurrence in any node, including
    // one the typed layer would not attribute to a node at all. Commented-out lines are not
    // matched: the key has to stand at the start of its line, which is also why the yaml headers
    // may discuss the property in prose.
    [Fact]
    public void ArBeYamls_ConfigureDeleteAfterSuccessExactlyOnce()
    {
        var violations = new List<string>();
        var checkedYamls = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", yaml)));
            if (!raw.Contains("DilosFileConfirm@1", StringComparison.Ordinal))
            {
                continue; // no DILOS return path in this yaml (as/ck/ai)
            }

            checkedYamls++;
            var occurrences = Regex.Matches(raw, @"(?m)^\s*deleteAfterSuccess\s*:").Count;
            if (occurrences != 1)
            {
                violations.Add($"{yaml}: deleteAfterSuccess is configured {occurrences} time(s) - it belongs " +
                               "on DilosFileGate@1 and nowhere else, or two places can disagree again");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, checkedYamls); // ar + be
    }

    // ---------- contract: AR/BE read their files as Latin-1 ----------

    // The mirror image of the delivery pin further down. The retired fetch node decoded with
    // DilosFile.Encoding; SftpDownload@1 defaults to utf-8, so a yaml that leaves the property out
    // turns every Latin-1 umlaut byte into a replacement character - and the run stays green,
    // because a replacement character is a perfectly valid string.
    [Fact]
    public async Task ArBeYamls_ReadDilosFilesAsIso88591()
    {
        var violations = new List<string>();
        var downloads = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);

            foreach (var download in Walk(root.Transformations).OfType<SftpDownloadNodeConfiguration>())
            {
                downloads++;

                // Read from the BOUND configuration, so a property left out of the yaml is caught
                // on its default rather than passing because the text happens to mention it.
                var configured = Encoding.GetEncoding(download.Encoding);
                if (configured.CodePage != DilosFile.Encoding.CodePage)
                {
                    violations.Add($"{yaml}: SftpDownload '{download.Description}' resolves " +
                                   $"'{download.Encoding}' to code page {configured.CodePage}, but DILOS " +
                                   $"files are written in {DilosFile.Encoding.CodePage}");
                }
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, downloads); // ar + be
    }

    // ---------- contract 9: DilosFileConfirm@1 is the LAST child of the per-file ForEach ----------

    // Child order IS execution order (middleware chain — a throw aborts the remainder): if the
    // confirm ever moved before the write, keep mode would mark a file kept before its write ran
    // (a later write failure then skips the file on every future tick), and delete mode would
    // delete the LKV file before the write — until this test the invariant lived only in the
    // yaml comments ("DilosFileConfirm@1 is the LAST child").
    [Fact]
    public async Task ArBeYamls_DilosFileConfirm_IsTheLastPerFileForEachChild()
    {
        var violations = new List<string>();
        var checkedYamls = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            var nodes = Walk(root.Transformations).ToList();
            if (!nodes.OfType<DilosFileConfirmNodeConfiguration>().Any())
            {
                continue; // no DILOS return-path confirm in this yaml (as/ck/ai)
            }

            checkedYamls++;
            var forEach = Assert.Single(nodes.OfType<ForEachNodeConfiguration>());
            var children = forEach.Transformations?.ToList() ?? new List<NodeConfiguration>();

            if (children.Count(c => c is DilosFileConfirmNodeConfiguration) != 1)
            {
                violations.Add($"{yaml}: exactly ONE DilosFileConfirm@1 must confirm each file element");
            }

            if (children.Count == 0 || children[^1] is not DilosFileConfirmNodeConfiguration)
            {
                violations.Add($"{yaml}: DilosFileConfirm@1 must be the LAST child of the per-file " +
                               "ForEach — anything after it would run on an already confirmed (possibly " +
                               "deleted) file, anything before the write chain confirms an unwritten file");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, checkedYamls); // ar + be — the return-path yamls must not lose the confirm
    }

    // ---------- contract: WeClapp access comes ONLY from the tenant GlobalConfiguration ----------

    // Since AB#4845 every pipeline references the tenant entry via apiConfiguration; the inline
    // baseUrl/apiKey properties still exist on the nodes as back-compat, so nothing but this test
    // stops a "quick tenant test" commit from putting a literal API key back into a shipped
    // pipeline definition. Raw-text scan on purpose: it catches commented-out leftovers too and
    // does not depend on the local SDK feed being able to deserialize the yamls.
    [Fact]
    public void AllPipelineYamls_UseApiConfigurationOnly_NoInlineCredentialsOrPlaceholders()
    {
        Assert.True(AllPipelineYamls.Length >= 5,
            "pipeline yaml enumeration must find the shipped pipelines");

        foreach (var yaml in AllPipelineYamls)
        {
            var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", yaml)));

            // Key-position syntax, including YAML-legal spellings a plain substring would miss
            // ("apiKey :", quoted "apiKey"). The entry-shape note "{ baseUrl, apiKey }" in the
            // apiConfiguration comments carries no colon and is legitimate documentation.
            Assert.DoesNotMatch("(?i)[\"']?\\bapiKey[\"']?\\s*:", raw);
            Assert.DoesNotMatch("(?i)[\"']?\\bbaseUrl[\"']?\\s*:", raw);
            // Deliberately absolute — never re-document the retired ${...} substitution
            // mechanism inside a pipeline yaml, not even in a comment.
            Assert.DoesNotContain("${", raw);

            // Per-FILE gate (a raw-text scan cannot attribute properties to nodes): every yaml
            // with any WeClapp node — the legacy trigger included — must reference the tenant
            // entry. A per-NODE assert through the typed config layer should follow once the
            // local SDK feed is >= r3.4.91 and the ar/be yamls deserialize again.
            if (raw.Contains("WeClappFetchStep@1", StringComparison.Ordinal) ||
                raw.Contains("WeClappFetch@1", StringComparison.Ordinal) ||
                raw.Contains("MakeHttpRequest@1", StringComparison.Ordinal) ||
                raw.Contains("WeClappArWrite@1", StringComparison.Ordinal) ||
                raw.Contains("WeClappBeWrite@1", StringComparison.Ordinal))
            {
                // Line-anchored on purpose. A plain substring — and any unanchored regex —
                // is also satisfied by a COMMENT mentioning the entry, which would green-light
                // a yaml whose node carries no apiConfiguration at all. This tolerates
                // indentation, extra whitespace, quoting and a trailing comment, but keeps the
                // casing pinned and forces the value to END here, so a renamed entry
                // ("WeClappApiOld") does not pass as a prefix match.
                Assert.Matches(@"(?m)^\s*apiConfiguration\s*:\s*[""']?WeClappApi[""']?\s*(#.*)?$", raw);
            }
        }
    }

    // ---------- contract: a dry-run write node forbids deleting the source file ----------

    // dryRun and deleteAfterSuccess are coupled by OPERATIONS, not by code: WeClappArWrite@1 /
    // WeClappBeWrite@1 resolve their dry run as `config.DryRun || PipelineExecutionMode.IsDryRun`,
    // while DilosFileConfirmNode only looks at PipelineExecutionMode - it never sees the write
    // node's dryRun. A normal (non-dry-run) execution with dryRun: true and deleteAfterSuccess:
    // true therefore writes nothing, reports success and still DELETES the remote file: the LKV
    // copy is consumed although its content never reached WeClapp, and that copy is the only
    // source. Until now the yaml comments were the sole guard against that combination. Raw text
    // for the same reason as the apiConfiguration gate: the typed path needs an SDK feed
    // >= r3.4.91 before the ar/be yamls deserialize again.
    [Fact]
    public void ArBeYamls_DryRunWriteNode_ForbidsDeleteAfterSuccess()
    {
        var violations = new List<string>();
        var checkedYamls = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", yaml)));
            if (!raw.Contains("DilosFileConfirm@1", StringComparison.Ordinal))
            {
                continue; // no confirm node - this yaml deletes no source file (as/ck/ai)
            }

            checkedYamls++;

            if (!Regex.IsMatch(raw, @"(?m)^\s*dryRun\s*:\s*true\s*(#.*)?$"))
            {
                continue; // go-live mode: deleting is intended, the parity test guards the rest
            }

            var deleting = Regex.Matches(raw, @"(?m)^\s*deleteAfterSuccess\s*:\s*(\S+)")
                .Where(m => !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (deleting.Count > 0)
            {
                violations.Add(
                    $"{yaml}: a write node runs with dryRun: true while {deleting.Count} " +
                    "deleteAfterSuccess value(s) are not false - the write would be skipped and " +
                    "DilosFileConfirm@1 (which never sees the write node's dryRun) would still " +
                    "delete the LKV file");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, checkedYamls); // ar + be - the return-path yamls must stay covered
    }

    // ---------- contract 13: AS/AI deliver through the product node in Latin-1 ----------

    // The DILOS file format is ISO-8859-1 and SftpUpload@1 defaults to utf-8, so a delivery node
    // that loses the property writes umlauts as two bytes and the LKV import sees mojibake -
    // silently, because nothing fails. Replace keeps the historic behaviour of one '?' per
    // unrepresentable scalar; Fail would drop a whole day's delivery over a single character.
    // The retired custom delivery node needs no check of its own any more: its configuration type
    // is gone, so a yaml naming it no longer reaches this loop at all - DeserializePipeline below
    // throws PipelineSerializationException ("Unknown discriminator ...", verified on the yaml),
    // which reds every contract test at once instead of adding one violation here.
    [Fact]
    public async Task AsAiYamls_DeliverViaSftpUploadInIso88591()
    {
        var violations = new List<string>();
        var uploads = 0;

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);

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
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' uses onEncodingError " +
                                   $"'{upload.OnEncodingError}', expected 'Replace' - Fail would suppress " +
                                   "the whole delivery over one character");
                }

                // The yaml names the encoding as a string, DilosRender builds the content with
                // DilosFile.Encoding. Two names that resolve to different code pages would still
                // upload - with different bytes, which the LKV import reads as mojibake.
                var configured = Encoding.GetEncoding(upload.Encoding);
                if (configured.CodePage != DilosFile.Encoding.CodePage)
                {
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' resolves '{upload.Encoding}' to " +
                                   $"code page {configured.CodePage}, but the render side writes " +
                                   $"{DilosFile.Encoding.CodePage}");
                }

                // The retired custom node had no static-name and no binary-source property, so the
                // file name COULD only come from the render step. The product node offers both, and
                // a static name would make every delivery overwrite the same remote file.
                if (string.IsNullOrEmpty(upload.FileNamePath))
                {
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' has no fileNamePath - " +
                                   "the delivered name must come from the render step");
                }

                if (!string.IsNullOrEmpty(upload.FileName) || !string.IsNullOrEmpty(upload.FileRtId) ||
                    !string.IsNullOrEmpty(upload.FileRtIdPath))
                {
                    violations.Add($"{yaml}: SftpUpload '{upload.Description}' names a static file name or a " +
                                   "binary source - DILOS content comes from the data context via path, and a " +
                                   "static name would overwrite the previous delivery");
                }
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, uploads); // as + ai - a third delivery must be a deliberate edit here
    }

    // ---------- contract 14: the AS/AI delivery reads the render output and targets LKV ----------

    // Contract 13 pins HOW the delivery encodes, not WHAT it reads or WHERE it writes. Each of
    // the strings below can be renamed on ONE side and still ship green: an upload whose path no
    // longer matches the render's targetPath reads an unset value, one whose fileNamePath no
    // longer matches fileNameTargetPath has no name, a remoteDirectory other than the root puts
    // the file where the LKV import does not look, and a serverConfiguration drifting away from
    // the return path's entry delivers to a different server than the AR/BE files come from.
    // None of it fails locally - the first evidence would be a staging run.
    [Fact]
    public async Task AsAiYamls_SftpUpload_ReadsTheRenderOutputAndTargetsTheLkvRoot()
    {
        var violations = new List<string>();
        var deliveries = 0;
        var sftpEntries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            var nodes = Walk(root.Transformations).ToList();

            // The return path names its entry on the product nodes now. Reading it off the
            // retired fetch step instead would leave this assertion trivially satisfied by the
            // two deliveries alone, while its message still claimed to cover both directions.
            foreach (var list in nodes.OfType<SftpListNodeConfiguration>())
            {
                sftpEntries.Add(list.ServerConfiguration);
            }

            foreach (var download in nodes.OfType<SftpDownloadNodeConfiguration>())
            {
                sftpEntries.Add(download.ServerConfiguration);
            }

            var uploads = nodes.OfType<SftpUploadNodeConfiguration>().ToList();
            if (uploads.Count == 0)
            {
                continue; // no DILOS delivery in this yaml (ck/ar/be)
            }

            deliveries++;
            var upload = Assert.Single(uploads);
            var render = Assert.Single(nodes.OfType<DilosRenderNodeConfiguration>());
            sftpEntries.Add(upload.ServerConfiguration);

            if (!string.Equals(render.TargetPath, upload.Path, StringComparison.Ordinal))
            {
                violations.Add($"{yaml}: DilosRender writes the content to '{render.TargetPath}' but " +
                               $"SftpUpload reads '{upload.Path}'");
            }

            if (!string.Equals(render.FileNameTargetPath, upload.FileNamePath, StringComparison.Ordinal))
            {
                violations.Add($"{yaml}: DilosRender writes the file name to '{render.FileNameTargetPath}' " +
                               $"but SftpUpload reads '{upload.FileNamePath}'");
            }

            if (upload.RemoteDirectory != "/")
            {
                violations.Add($"{yaml}: SftpUpload delivers to '{upload.RemoteDirectory}', expected the SFTP " +
                               "root - that is the directory the LKV import reads (Billbee production layout)");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(2, deliveries); // as + ai - a third delivery must be a deliberate edit here
        Assert.True(sftpEntries.Count == 1,
            "the AS/AI delivery and the AR/BE return path must name the SAME tenant SFTP entry, found: " +
            string.Join(", ", sftpEntries.OrderBy(e => e, StringComparer.Ordinal)));
    }

    // ---------- contract 15: entity persistence uses the current ApplyChanges ----------

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
                               "use @2 with entityUpdatesPath (and associationUpdatesPath where needed)");
            }
        }

        Assert.Empty(violations);
    }

    // ---------- contract: the source pipelines fail loudly on an HTTP error ----------

    // MakeHttpRequest@1 defaults to LogAndStop: it logs, skips the rest of the chain and the
    // execution finishes GREEN. The node these pipelines replaced threw, and the operational
    // alerting is built on failed executions - so a default left in place here would turn every
    // WeClapp outage into a silent no-delivery.
    [Fact]
    public async Task SourceYamls_EveryMakeHttpRequest_FailsLoudlyOnHttpErrors()
    {
        foreach (var file in new[]
                 {
                     "weclapp-articles-to-as.yaml", "weclapp-articles-to-ck.yaml",
                     "weclapp-orders-to-ai.yaml",
                 })
        {
            var root = await DeserializePipeline(file);
            var requests = Walk(root.Transformations).OfType<MakeHttpRequestNodeConfiguration>().ToList();
            Assert.NotEmpty(requests);
            Assert.All(requests, request =>
                Assert.Equal(HttpErrorHandling.Throw, request.OnHttpError));
        }
    }

    // ---------- contract: paged requests read WeClapp's result envelope ----------

    // Every WeClapp entity response wraps its elements in a top-level "result" array. An
    // itemsPath that addresses anything else fails the run rather than reading zero elements,
    // but only once it runs - this pins it at build time.
    [Fact]
    public async Task SourceYamls_PagedMakeHttpRequest_ReadsTheWeclappResultArray()
    {
        foreach (var file in new[]
                 {
                     "weclapp-articles-to-as.yaml", "weclapp-articles-to-ck.yaml",
                     "weclapp-orders-to-ai.yaml",
                 })
        {
            var root = await DeserializePipeline(file);
            var paged = Walk(root.Transformations)
                .OfType<MakeHttpRequestNodeConfiguration>()
                .Where(request => request.Paging is not null)
                .ToList();
            Assert.NotEmpty(paged);
            Assert.All(paged, request => Assert.Equal("$.result", request.Paging!.ItemsPath));
        }
    }

    // ---------- contract: the ai fetch only ever sees confirmed orders ----------

    // The customer's historical order stock is CLOSED, and the dedup gate further down stops
    // repeat deliveries only - a first-time delivery always passes it. This status filter is
    // therefore the single thing between one tick and the whole order backlog landing on LKV's
    // SFTP, and it lives inside a url that reads like an ordinary query: deleting it changes
    // nothing that fails, neither here nor at the tenant.
    [Fact]
    public async Task OrdersToAiYaml_PagedOrderRequest_FiltersOnConfirmedOrders()
    {
        var root = await DeserializePipeline("weclapp-orders-to-ai.yaml");
        var paged = Assert.Single(
            Walk(root.Transformations).OfType<MakeHttpRequestNodeConfiguration>(),
            request => request.Paging is not null);

        Assert.Contains("/salesOrder", paged.Url, StringComparison.Ordinal);
        Assert.Contains("status-eq=ORDER_CONFIRMATION_PRINTED", paged.Url, StringComparison.Ordinal);
    }

    // ---------- contract: the ai customer lookup feeds the order transform ----------

    // Three strings have to agree for an AI file to carry a recipient: the lookup's targetPath,
    // the transform's customerPath, and the order of the two children. Any one of them can be
    // edited alone and still ship green - and the run would then fail per order at the earliest,
    // on staging.
    [Fact]
    public async Task OrdersToAiYaml_CustomerLookupFeedsTheOrderTransform()
    {
        var root = await DeserializePipeline("weclapp-orders-to-ai.yaml");
        var loop = Assert.Single(Walk(root.Transformations).OfType<ForEachNodeConfiguration>());
        var children = loop.Transformations!.ToList();

        var lookup = Assert.IsType<MakeHttpRequestNodeConfiguration>(children[0]);
        var toCk = Assert.Single(children.OfType<WeClappToCkNodeConfiguration>());

        Assert.Equal("$.current", toCk.Path);
        Assert.StartsWith(lookup.TargetPath + ".", toCk.CustomerPath);
        // The lookup addresses THIS order's customer, not a static one.
        Assert.Contains(lookup.PathParameters,
            parameter => parameter.ValuePath == "$.current.customerId");
    }

    // ---------- contract: one poisoned order cannot starve the others ----------

    // The acceptance case: the customer of order 2 fails permanently, orders 1 and 3 are still
    // delivered, the execution still fails so the alerting sees it, and order 2 is picked up on
    // the next tick because it wrote no marker. continueOnError is the isolation half of that
    // and can be removed by deleting one line.
    [Fact]
    public async Task OrdersToAiYaml_ForEachIsolatesAFailingOrder()
    {
        var root = await DeserializePipeline("weclapp-orders-to-ai.yaml");
        var loop = Assert.Single(Walk(root.Transformations).OfType<ForEachNodeConfiguration>());

        Assert.True(loop.ContinueOnError,
            "a permanently failing customer must fail its own order, not the whole tick");
    }

    // The guard above keys on node names, and this change rewrote them. A file that drops out of
    // that trigger stops being checked WITHOUT failing - so the three source pipelines are also
    // pinned by name here. Keying the trigger itself on "the file mentions apiConfiguration"
    // would be circular: the one file that forgot the entry would be the one file never checked.
    [Fact]
    public void SourceYamls_AreCoveredByTheApiConfigurationGuard()
    {
        foreach (var file in new[]
                 {
                     "weclapp-articles-to-as.yaml", "weclapp-articles-to-ck.yaml",
                     "weclapp-orders-to-ai.yaml", "dilos-ar-to-weclapp.yaml",
                     "dilos-be-to-weclapp.yaml",
                 })
        {
            var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", file)));
            Assert.Matches(@"(?m)^\s*apiConfiguration\s*:\s*[""']?WeClappApi[""']?\s*(#.*)?$", raw);
        }
    }

    // ---------- helpers ----------

    private static async Task<NodeDefinitionRoot> DeserializePipeline(string fileName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline()
            .AddMeshDataPipelineNodes()
            .RegisterNodeConfiguration<IfNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchTriggerNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappToCkNodeConfiguration>()
            .RegisterNodeConfiguration<DilosRenderNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappArWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappBeWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchStepNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileFetchStepNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileGateNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileConfirmNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappResolveSupplySourcesNodeConfiguration>()
            .RegisterNodeConfiguration<DilosExportRunKeyNodeConfiguration>();
        var lookup = services.BuildServiceProvider().GetRequiredService<INodeQualifiedNameLookupService>();

        await using var stream = File.OpenRead(FindRepoFile(Path.Combine("pipelines", fileName)));
        return await new YamlPipelineConfigurationSerializer(lookup).DeserializeAsync(stream)
               ?? throw new InvalidOperationException($"'{fileName}' deserialized to null");
    }

    private static async Task<IDataContext> RunToCkNode(WeClappToCkNodeConfiguration config, string documentJson)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(documentJson));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("WeClappToCk", 0, config, dataContext);

        await new WeClappToCkNode(A.Fake<NodeDelegate>()).ProcessObjectAsync(dataContext, nodeContext);
        return dataContext;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"'{relativePath}' not found above {AppContext.BaseDirectory}");
    }
}
