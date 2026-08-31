using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Reads a SHIPPED pipeline yaml with the platform's own strict serializer, registering exactly
/// the node configurations the adapter declares. Shared so that every suite asserting against a
/// pipeline definition sees the same registration set: the deserializer refuses an unregistered
/// node type, so a suite with its own narrower set would fail on a yaml that is perfectly valid
/// for the tenant.
/// </summary>
internal static class PipelineDefinitions
{
    internal static async Task<NodeDefinitionRoot> DeserializeAsync(string fileName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline()
            .AddMeshDataPipelineNodes()
            .RegisterNodeConfiguration<IfNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappToCkNodeConfiguration>()
            .RegisterNodeConfiguration<DilosRenderNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappArWriteNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappBeWriteNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileGateNodeConfiguration>()
            .RegisterNodeConfiguration<DilosFileConfirmNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappResolveSupplySourcesNodeConfiguration>()
            .RegisterNodeConfiguration<DilosExportRunKeyNodeConfiguration>();
        var lookup = services.BuildServiceProvider().GetRequiredService<INodeQualifiedNameLookupService>();

        await using var stream = File.OpenRead(RepoFiles.Find(Path.Combine("pipelines", fileName)));
        return await new YamlPipelineConfigurationSerializer(lookup).DeserializeAsync(stream)
               ?? throw new InvalidOperationException($"'{fileName}' deserialized to null");
    }
}
