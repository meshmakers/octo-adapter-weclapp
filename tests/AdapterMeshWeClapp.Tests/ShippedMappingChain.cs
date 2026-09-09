using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Runs the MAPPING slice of a shipped ck/ai yaml with the real SDK nodes on a real DataContextImpl:
/// the per-item If@1 gate (the system-record filter) with its children up to, but excluding, the
/// first repository node. The customer lookup is skipped as well - its response is part of the
/// document the caller seeds ($.customerResponse), exactly the shape the loop body holds after the
/// request. Reading the pipeline definition instead of restating it is what makes the callers tests
/// of the shipped mapping rather than tests of the nodes.
/// </summary>
internal static class ShippedMappingChain
{
    internal static async Task<IDataContext> RunAsync(string yamlFileName, string documentJson)
    {
        var slice = await MappingSliceAsync(yamlFileName);

        var dataContext = new DataContextImpl(JsonDocument.Parse(documentJson));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();   // registers every standard node the slice instantiates
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);

        await new IfNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(dataContext, rootContext.RegisterChildNode("If", 0, slice, dataContext));
        return dataContext;
    }

    /// <summary>The per-item gate with only the mapping nodes as its children.</summary>
    internal static async Task<IfNodeConfiguration> MappingSliceAsync(string yamlFileName)
    {
        var root = await PipelineDefinitions.DeserializeAsync(yamlFileName);
        var loop = Assert.Single(Walk(root.Transformations).OfType<ForEachNodeConfiguration>());
        var gate = Assert.IsType<IfNodeConfiguration>(Assert.Single(loop.Transformations!));
        var mapping = (gate.Transformations ?? [])
            .TakeWhile(node => node is not GetOrCreateRtEntitiesByTypeNodeConfiguration)
            .Where(node => node is not MakeHttpRequestNodeConfiguration)
            .ToList();
        Assert.NotEmpty(mapping);
        return gate with { Transformations = mapping };
    }
}
