using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Deep pipeline-configuration walker shared by the YAML guard suites — descends into EVERY
/// children-bearing container (If/Switch/ForEach/For/Group/…, via
/// <see cref="IChildNodeConfiguration"/>) plus the Switch-specific Cases/Default collections,
/// so no node can hide from a guard inside a nested container.
/// </summary>
internal static class PipelineYamlWalk
{
    internal static IEnumerable<NodeConfiguration> Walk(IEnumerable<NodeConfiguration>? nodes)
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
}
