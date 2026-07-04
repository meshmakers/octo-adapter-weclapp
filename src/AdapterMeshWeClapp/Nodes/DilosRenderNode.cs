using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosRender node. Reads an array of WeClapp objects from
/// <c>Path</c> and writes the rendered DILOS file content (pipe-delimited, CRLF)
/// to <c>TargetPath</c>.
/// </summary>
[NodeName("DilosRender", 1)]
public record DilosRenderNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>Which DILOS file type to render: "AS" (article master, A* lines from
    /// WeClapp articles) or "AI" (order import, K*/P* lines from WeClapp sales orders).</summary>
    public required string Mode { get; set; }

    /// <summary>WeClapp Mandanten-ID → DILOS "Submandant" (constant per tenant; LKV maps it).
    /// Required for mode AI, unused for AS.</summary>
    public string Submandant { get; set; } = "";
}

/// <summary>
/// Renders WeClapp objects from the pipeline data context into DILOS file content
/// (custom node #3 of the ingestion design). Filtering of system records is the
/// upstream WeClappToCk node's responsibility — this node renders what it receives.
/// </summary>
[NodeConfiguration(typeof(DilosRenderNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosRenderNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>();

        var content = config.Mode switch
        {
            "AS" => RenderArticles(dataContext, config),
            "AI" => RenderOrders(dataContext, config),
            _ => throw new WeClappPipelineExecutionException(
                $"Unknown DilosRender mode '{config.Mode}' (expected 'AS' or 'AI')"),
        };

        dataContext.Set(config.TargetPath, content, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        nodeContext.Info($"DilosRender: rendered {config.Mode} content ({content.Length} chars)");

        await next(dataContext, nodeContext);
    }

    private static string RenderArticles(IDataContext dataContext, DilosRenderNodeConfiguration config)
    {
        var articles = dataContext.GetArray<WeClappArticle>(config.Path)
                       ?? throw new WeClappPipelineExecutionException(
                           $"No article array found at path '{config.Path}'");

        var lines = articles
            .OfType<WeClappArticle>()
            .Select(a => DilosArticleWriter.RenderLine(a, DilosArticleContext.Default));

        return JoinCrlf(lines);
    }

    private static string RenderOrders(IDataContext dataContext, DilosRenderNodeConfiguration config)
    {
        if (config.Submandant.Length == 0)
        {
            throw new WeClappPipelineExecutionException("DilosRender mode 'AI' requires Submandant");
        }

        var orders = dataContext.GetArray<WeClappSalesOrder>(config.Path)
                     ?? throw new WeClappPipelineExecutionException(
                         $"No order array found at path '{config.Path}'");

        var ctx = new DilosOrderContext { Submandant = config.Submandant };
        var lines = orders
            .OfType<WeClappSalesOrder>()
            .SelectMany(o => new[] { DilosOrderWriter.RenderHeader(o, ctx) }
                .Concat(DilosOrderWriter.RenderPositions(o, ctx)));

        return JoinCrlf(lines);
    }

    private static string JoinCrlf(IEnumerable<string> lines)
    {
        var joined = string.Join("\r\n", lines);
        return joined.Length == 0 ? "" : joined + "\r\n";
    }
}
