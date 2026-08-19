using System.Reflection;
using System.Text.Json;
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
            {"current":{"item":{"id":"168914","articleNumber":"TW_Z_074","name":"Ersatz Schnellverschlüsse",
             "articleType":"STORABLE","ean":"9001234567890","active":true}}}
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
            {"current":{"item":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customer":{"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}}}
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
            {"current":{"item":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customer":{"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}}}
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
    [InlineData("weclapp-articles-to-as.yaml", typeof(WeClappFetchStepNodeConfiguration))]
    [InlineData("weclapp-articles-to-ck.yaml", typeof(WeClappFetchStepNodeConfiguration))]
    [InlineData("weclapp-orders-to-ai.yaml", typeof(WeClappFetchStepNodeConfiguration))]
    [InlineData("dilos-ar-to-weclapp.yaml", typeof(DilosFileFetchStepNodeConfiguration))]
    [InlineData("dilos-be-to-weclapp.yaml", typeof(DilosFileFetchStepNodeConfiguration))]
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

    // ---------- contract 8: DilosFileFetchStep and DilosFileConfirm agree on deleteAfterSuccess + serverConfiguration ----------

    // The two nodes read/write the SAME DilosFileFetchState keys for one file element (the step
    // gates the keep-mode skip / pending-delete retry, the confirm node performs the actual
    // first-time delete) — every ar/be yaml already carries a comment saying deleteAfterSuccess
    // MUST match; this machine-guards that invariant instead of leaving it comment-only, so the
    // go-live flip from false to true cannot update one node and forget the other.
    // serverConfiguration must also match — a drift there would mean DilosFileConfirm@1 deleting
    // (or marking kept) against a DIFFERENT SFTP server than the one DilosFileFetchStep@1 listed
    // the file from.
    [Fact]
    public async Task AllPipelineYamls_DilosFileFetchStepAndConfirm_DeleteAfterSuccessMatches()
    {
        var violations = new List<string>();

        foreach (var yaml in AllPipelineYamls)
        {
            var root = await DeserializePipeline(yaml);
            var nodes = Walk(root.Transformations).ToList();
            var fetchSteps = nodes.OfType<DilosFileFetchStepNodeConfiguration>().ToList();
            var confirms = nodes.OfType<DilosFileConfirmNodeConfiguration>().ToList();

            if (fetchSteps.Count == 0 && confirms.Count == 0)
            {
                continue; // no DILOS return-path file nodes in this yaml at all (as/ck/ai)
            }

            var fetchStep = Assert.Single(fetchSteps);
            var confirm = Assert.Single(confirms);

            if (fetchStep.DeleteAfterSuccess != confirm.DeleteAfterSuccess)
            {
                violations.Add($"{yaml}: DilosFileFetchStep@1.deleteAfterSuccess=" +
                                $"{fetchStep.DeleteAfterSuccess} but DilosFileConfirm@1.deleteAfterSuccess=" +
                                $"{confirm.DeleteAfterSuccess} — the two must match or files get stuck");
            }

            if (fetchStep.ServerConfiguration != confirm.ServerConfiguration)
            {
                violations.Add($"{yaml}: DilosFileFetchStep@1.serverConfiguration=" +
                                $"'{fetchStep.ServerConfiguration}' but DilosFileConfirm@1.serverConfiguration=" +
                                $"'{confirm.ServerConfiguration}' — deleting/marking on a different server than listed");
            }
        }

        Assert.Empty(violations);
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

            // Property syntax with colon: the entry-shape note "{ baseUrl, apiKey }" in the
            // apiConfiguration comments is legitimate documentation and must not trip this.
            Assert.DoesNotContain("apiKey:", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("baseUrl:", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("${", raw); // no substitution placeholders of any kind

            if (raw.Contains("WeClappFetchStep@1", StringComparison.Ordinal) ||
                raw.Contains("WeClappArWrite@1", StringComparison.Ordinal) ||
                raw.Contains("WeClappBeWrite@1", StringComparison.Ordinal))
            {
                Assert.Contains("apiConfiguration: WeClappApi", raw, StringComparison.Ordinal);
            }
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
            .RegisterNodeConfiguration<DilosSftpWriteNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappArWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappBeWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchStepNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileFetchStepNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileConfirmNodeConfiguration>();
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
