using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosRender node. Reads an array of WeClapp objects from
/// <c>Path</c> and writes the rendered DILOS file content (pipe-delimited, LF —
/// all real Billbee-produced AS/AI files are pure LF, the DILOS-import-proven
/// format; CRLF only occurs in files DILOS itself produces) to <c>TargetPath</c>.
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

    /// <summary>Optional JSONPath to receive the golden DILOS file name (consumed by
    /// DilosSftpWrite's <c>fileNamePath</c>): AI → "AI{Auftragsnummer1}.txt" (= the WeClapp
    /// id, matching the K* line; exactly one order required), AS → "AS{yyyyMMddHHmmss}.txt"
    /// in Vienna local time. Empty = no name is written.</summary>
    public string FileNameTargetPath { get; set; } = "";
}

/// <summary>
/// Renders WeClapp objects from the pipeline data context into DILOS file content
/// (custom node #3 of the ingestion design). Filtering of system records is the
/// upstream WeClappToCk node's responsibility — this node renders what it receives.
/// </summary>
[NodeConfiguration(typeof(DilosRenderNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosRenderNode(NodeDelegate next, TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>();

        string content;
        var fileName = "";

        switch (config.Mode)
        {
            case "AS":
                content = RenderArticles(dataContext, config, nodeContext);
                if (config.FileNameTargetPath.Length > 0)
                {
                    fileName = DilosFile.AsFileName(_timeProvider.GetUtcNow());
                }

                break;

            case "AI":
                {
                    var orders = ReadOneOrMany<WeClappSalesOrder>(dataContext, config.Path, "order");
                    content = RenderOrders(orders, config);
                    if (config.FileNameTargetPath.Length > 0)
                    {
                        fileName = BuildAiFileName(orders);
                    }

                    break;
                }

            default:
                throw new WeClappPipelineExecutionException(
                    $"Unknown DilosRender mode '{config.Mode}' (expected 'AS' or 'AI')");
        }

        dataContext.Set(config.TargetPath, content, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        if (fileName.Length > 0)
        {
            dataContext.Set(config.FileNameTargetPath, fileName, config.DocumentMode,
                ValueKinds.Simple, TargetValueWriteModes.Overwrite);
        }

        nodeContext.Info("DilosRender: rendered {0} content ({1} chars){2}",
            config.Mode, content.Length, fileName.Length > 0 ? $" as '{fileName}'" : "");

        await next(dataContext, nodeContext);
    }

    private static string RenderArticles(IDataContext dataContext, DilosRenderNodeConfiguration config,
        INodeContext nodeContext)
    {
        var articles = ReadOneOrMany<WeClappArticle>(dataContext, config.Path, "article");

        // System articles (loading equipment such as pallets) never belong in the AS master
        // data file. The combined pipeline used to drop them via the upstream WeClappToCk
        // node; the dedicated AS delivery pipeline has no CK stage, so the render owns it.
        var deliverable = articles.Where(a => !WeClappToDilos.IsSystemArticle(a)).ToList();
        if (deliverable.Count < articles.Count)
        {
            nodeContext.Info("DilosRender: skipped {0} system article(s) (loading equipment)",
                articles.Count - deliverable.Count);
        }

        var lines = deliverable
            .Select(a => DilosArticleWriter.RenderLine(a, DilosArticleContext.Default));

        return JoinLf(lines);
    }

    private static string RenderOrders(List<WeClappSalesOrder> orders, DilosRenderNodeConfiguration config)
    {
        if (config.Submandant.Length == 0)
        {
            throw new WeClappPipelineExecutionException("DilosRender mode 'AI' requires Submandant");
        }

        var ctx = new DilosOrderContext { Submandant = config.Submandant };
        var lines = orders
            .SelectMany(o => new[] { DilosOrderWriter.RenderHeader(o, ctx) }
                .Concat(DilosOrderWriter.RenderPositions(o, ctx)));

        return JoinLf(lines);
    }

    /// <summary>The AI name is defined per single order (golden: one file per order), so a
    /// batch here is a config error; the name format itself lives in Core (DilosFile) next
    /// to the writers, keeping it in lockstep with the K* line's Auftragsnummer1.</summary>
    private static string BuildAiFileName(List<WeClappSalesOrder> orders)
    {
        if (orders.Count != 1)
        {
            throw new WeClappPipelineExecutionException(
                $"AI file name requires exactly one order per execution, got {orders.Count} " +
                "(golden precedent: one AI file per order)");
        }

        var auftragsnummer1 = orders[0].Id;
        if (string.IsNullOrEmpty(auftragsnummer1))
        {
            throw new WeClappPipelineExecutionException(
                "Order has no id (Auftragsnummer1) — cannot build the AI file name");
        }

        return DilosFile.AiFileName(auftragsnummer1);
    }

    /// <summary>Reads the source as an array OR a single object — per-document pipelines
    /// (one order per execution; golden AI files are one file per order) carry one object.</summary>
    private static List<T> ReadOneOrMany<T>(IDataContext dataContext, string path, string what)
        where T : class
    {
        if (dataContext.GetKind(path) == DataKind.Object)
        {
            var single = dataContext.Get<T>(path)
                         ?? throw new WeClappPipelineExecutionException(
                             $"No {what} found at path '{path}'");
            return new List<T> { single };
        }

        var many = dataContext.GetArray<T>(path)
                   ?? throw new WeClappPipelineExecutionException(
                       $"No {what} array found at path '{path}'");
        return many.OfType<T>().ToList();
    }

    private static string JoinLf(IEnumerable<string> lines)
    {
        var joined = string.Join("\n", lines);
        return joined.Length == 0 ? "" : joined + "\n";
    }
}
