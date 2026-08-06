using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Export dedup for the AI delivery pipeline: an order that already exists as a CK entity
/// has been delivered before (the entity is only persisted AFTER a successful upload), so
/// the whole render/upload/persist block is gated on the order lookup's ModOperation.
///
/// The gate tests write the ModOperation value into a REAL DataContextImpl exactly the way
/// GetOrCreateRtEntitiesByTypeNode does in production (same enum, same Set overload), so a
/// green test proves the If@1 configuration literals against production serialization.
/// The YAML test deserializes pipelines/weclapp-orders-to-ai.yaml with the platform's own
/// serializer and pins the at-least-once ordering (upload BEFORE persist, inside the gate).
/// </summary>
public class AiExportGateTests
{
    private const string ModOperationPath = "$.rt.orderModOperation";

    // ---------- gate semantics against production-faithful data-context writes ----------

    [Fact]
    public async Task IfGate_KnownOrder_SkipsChildTransformations()
    {
        var (dataContext, nodeContext, next) = PrepareGate(UpdateKind.Update);

        await new IfNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Null(dataContext.Get<int?>("$.probe"));
        A.CallTo(() => next.Invoke(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IfGate_NewOrder_RunsChildTransformations()
    {
        var (dataContext, nodeContext, next) = PrepareGate(UpdateKind.Insert);

        await new IfNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(1, dataContext.Get<int?>("$.probe"));
        A.CallTo(() => next.Invoke(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    private static (IDataContext, INodeContext, NodeDelegate) PrepareGate(UpdateKind lookupResult)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));

        // EXACTLY the write GetOrCreateRtEntitiesByTypeNode performs for the order lookup.
        dataContext.Set(ModOperationPath, lookupResult, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        var gateConfiguration = GateConfiguration(new List<NodeConfiguration>
        {
            new AiGateProbeNodeConfiguration { TargetPath = "$.probe" },
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline().RegisterNode<AiGateProbeNode>();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("If", 0, gateConfiguration, dataContext);

        return (dataContext, nodeContext, A.Fake<NodeDelegate>());
    }

    /// <summary>The gate configuration the pipeline YAML must use (single source in this test).</summary>
    private static IfNodeConfiguration GateConfiguration(ICollection<NodeConfiguration>? transformations) =>
        new()
        {
            Path = ModOperationPath,
            Operator = CompareOperator.Equal,
            Value = (int)UpdateKind.Insert,
            ValueType = AttributeValueTypesDto.Enum,
            Transformations = transformations,
        };

    // ---------- the shipped YAML pins the gate + the at-least-once ordering ----------

    [Fact]
    public async Task OrdersToAiYaml_GatesDeliveryOnNewOrder_AndPersistsOnlyAfterUpload()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline()
            .AddMeshDataPipelineNodes()          // GetOrCreate/CreateUpdateInfo/ApplyChanges/…
            .RegisterNodeConfiguration<IfNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchTriggerNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappToCkNodeConfiguration>()
            .RegisterNodeConfiguration<DilosRenderNodeConfiguration>()
            .RegisterNodeConfiguration<DilosSftpWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchStepNodeConfiguration>();
        var lookup = services.BuildServiceProvider().GetRequiredService<INodeQualifiedNameLookupService>();

        NodeDefinitionRoot root;
        await using (var stream = File.OpenRead(FindRepoFile(Path.Combine("pipelines", "weclapp-orders-to-ai.yaml"))))
        {
            root = await new YamlPipelineConfigurationSerializer(lookup).DeserializeAsync(stream)
                   ?? throw new InvalidOperationException("pipeline yaml deserialized to null");
        }

        var top = root.Transformations?.ToList() ?? new List<NodeConfiguration>();

        // WeClappFetchStep@1 seeds $.orders; the former per-execution chain now runs once
        // per element inside ForEach@1 (AB#4228 trigger separation) — everything below
        // descends into its children instead of the pipeline's top level.
        var forEach = Assert.Single(top.OfType<ForEachNodeConfiguration>());
        var perItem = forEach.Transformations?.ToList() ?? new List<NodeConfiguration>();

        // The read-only lookups stay OUTSIDE the gate…
        Assert.Contains(perItem, n => n is GetOrCreateRtEntitiesByTypeNodeConfiguration);
        // …but nothing that renders, delivers or persists may run unconditionally:
        Assert.DoesNotContain(perItem, n => n is DilosRenderNodeConfiguration);
        Assert.DoesNotContain(perItem, n => n is DilosSftpWriteNodeConfiguration);
        Assert.DoesNotContain(perItem, n => n is ApplyChangesNodeConfiguration2);
        Assert.DoesNotContain(perItem, n => n is CreateUpdateInfoNodeConfiguration);
        Assert.DoesNotContain(perItem, n => n is CreateAssociationUpdateNodeConfiguration);

        // One gate, configured exactly like the semantics tests prove it works:
        var gate = Assert.Single(perItem.OfType<IfNodeConfiguration>());
        var expected = GateConfiguration(transformations: null);
        Assert.Equal(expected.Path, gate.Path);
        Assert.Equal(expected.Operator, gate.Operator);
        Assert.Equal(expected.ValueType, gate.ValueType);
        Assert.Equal(Convert.ToInt32(expected.Value), Convert.ToInt32(gate.Value));

        // Inside the gate: collect updates, render, upload — and persist ONLY afterwards
        // (the CK order entity IS the export marker; at-least-once by construction).
        var children = gate.Transformations?.ToList() ?? new List<NodeConfiguration>();
        var renderIndex = children.FindIndex(n => n is DilosRenderNodeConfiguration);
        var uploadIndex = children.FindIndex(n => n is DilosSftpWriteNodeConfiguration);
        var persistIndex = children.FindIndex(n => n is ApplyChangesNodeConfiguration2);

        Assert.True(renderIndex >= 0, "DilosRender must run inside the gate");
        Assert.True(uploadIndex > renderIndex, "DilosSftpWrite must run inside the gate, after render");
        Assert.True(persistIndex > uploadIndex, "ApplyChanges (the export marker) must run AFTER the upload");
        Assert.Contains(children, n => n is CreateUpdateInfoNodeConfiguration);
        Assert.Contains(children, n => n is CreateAssociationUpdateNodeConfiguration);
        Assert.Equal(persistIndex, children.Count - 1);
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

// Minimal probe node so the gate tests can observe whether the If children actually ran.
[NodeName("AiGateProbe", 1)]
internal record AiGateProbeNodeConfiguration : TargetPathNodeConfiguration;

[NodeConfiguration(typeof(AiGateProbeNodeConfiguration))]
internal class AiGateProbeNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<AiGateProbeNodeConfiguration>();
        dataContext.Set(c.TargetPath, 1, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        await next(dataContext, nodeContext);
    }
}
