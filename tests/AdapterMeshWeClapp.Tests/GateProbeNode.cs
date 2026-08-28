using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Minimal probe node so a gate test can observe whether an If@1's children actually ran. Shared
/// by the AI and the AS gate suites: both drive the REAL IfNode over the literals the shipped yaml
/// carries, and the only thing they need to see is whether the branch was entered.
/// </summary>
[NodeName("GateProbe", 1)]
internal record GateProbeNodeConfiguration : TargetPathNodeConfiguration;

[NodeConfiguration(typeof(GateProbeNodeConfiguration))]
internal class GateProbeNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GateProbeNodeConfiguration>();
        dataContext.Set(c.TargetPath, 1, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        await next(dataContext, nodeContext);
    }
}
