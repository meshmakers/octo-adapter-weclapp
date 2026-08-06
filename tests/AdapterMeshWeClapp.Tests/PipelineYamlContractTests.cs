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
    private static string[] AllPipelineYamls =>
        Directory.GetFiles(Path.GetDirectoryName(FindRepoFile(Path.Combine("pipelines",
                "weclapp-articles-to-ck.yaml")))!, "*.yaml")
            .Select(Path.GetFileName)
            .Cast<string>()
            .ToArray();

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
        var transformations = root.Transformations?.ToList() ?? [];
        var toCk = Assert.Single(transformations.OfType<WeClappToCkNodeConfiguration>());
        var lookup = Assert.Single(transformations.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
        var updateInfo = Assert.Single(transformations.OfType<CreateUpdateInfoNodeConfiguration>());

        var dataContext = await RunToCkNode(toCk, """
            {"item":{"id":"168914","articleNumber":"TW_Z_074","name":"Ersatz Schnellverschlüsse",
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
        var transformations = root.Transformations?.ToList() ?? [];
        var toCk = Assert.Single(transformations.OfType<WeClappToCkNodeConfiguration>());
        var gate = Assert.Single(transformations.OfType<IfNodeConfiguration>());
        var customerUpdate = (gate.Transformations ?? [])
            .OfType<CreateUpdateInfoNodeConfiguration>()
            .Single(c => c.CkTypeId == "Industry.Logistics/Customer");
        var nameUpdate = (customerUpdate.AttributeUpdates ?? []).Single(u => u.AttributeName == "Name");

        // B2C: private customer without a company — exactly the case Jürgen reported
        // as an empty recipient name on 2026-07-16.
        var dataContext = await RunToCkNode(toCk, """
            {"item":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customer":{"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}}
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
            {"item":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customer":{"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}}
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

    [Theory]
    [InlineData("weclapp-articles-to-as.yaml")]
    public async Task ConvertedYaml_UsesPassiveTriggers_NoPollingFields(string file)
    {
        var root = await DeserializePipeline(file);
        Assert.Collection(root.Triggers!,
            t => Assert.IsType<FromPipelineTriggerEventNodeConfiguration>(t),
            t => Assert.IsType<FromExecutePipelineCommandNodeConfiguration>(t));
        var raw = File.ReadAllText(FindRepoFile(Path.Combine("pipelines", file)));
        Assert.DoesNotContain("pollingIntervalSeconds", raw);
        Assert.DoesNotContain("runOnStart", raw);
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

    // Descends into EVERY children-bearing container (If/Switch/ForEach/For/Group/…, via
    // IChildNodeConfiguration) plus the Switch-specific Cases/Default collections — a
    // CreateUpdateInfo hidden in any container must not escape the value-type guard.
    private static IEnumerable<NodeConfiguration> Walk(IEnumerable<NodeConfiguration>? nodes)
    {
        foreach (var node in nodes ?? [])
        {
            yield return node;

            if (node is IChildNodeConfiguration container)
            {
                foreach (var child in Walk(container.Transformations))
                {
                    yield return child;
                }
            }

            if (node is SwitchNodeConfiguration switchNode)
            {
                foreach (var child in Walk(switchNode.Default))
                {
                    yield return child;
                }

                foreach (var switchCase in switchNode.Cases)
                {
                    foreach (var child in Walk(switchCase.Transformations))
                    {
                        yield return child;
                    }
                }
            }
        }
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
