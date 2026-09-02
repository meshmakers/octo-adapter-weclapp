using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosRender node. Reads the WeClapp sales order from <c>Path</c> and
/// writes the rendered DILOS AI file content (pipe-delimited, LF) to <c>TargetPath</c>.
///
/// The two deliveries separate their records differently, and only the AI one is rendered here.
/// AI is LF, which the partner's own files show, and <c>JoinLf</c> below is where that lives.
/// The AS article master is contracted as CR+LF and is rendered by RenderDelimitedText@1 out of
/// weclapp-articles-to-as.yaml, not by this class - so the difference is a contract, not a
/// leftover, and neither delivery should be moved onto the other's separator.
/// </summary>
[NodeName("DilosRender", 1)]
public record DilosRenderNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>Which DILOS file type to render. "AI" (order import, K*/P* lines from WeClapp
    /// sales orders) is the only one left: the AS article master is plain column rendering and
    /// goes through the product's RenderDelimitedText@1 instead. The property stays so a pipeline
    /// definition keeps saying out loud what it renders, and an "AS" left in a yaml fails loudly
    /// here instead of delivering the wrong file.</summary>
    public required string Mode { get; set; }

    /// <summary>WeClapp Mandanten-ID mapped to the DILOS "Submandant" (constant per tenant; LKV
    /// maps it).</summary>
    public string Submandant { get; set; } = "";

    /// <summary>Optional JSONPath to receive the golden DILOS file name (consumed by
    /// SftpUpload@1's <c>fileNamePath</c>): "AI{Auftragsnummer1}.txt" - the WeClapp id, matching
    /// the K* line, which is why exactly one order per execution is required. Empty = no name is
    /// written.</summary>
    public string FileNameTargetPath { get; set; } = "";

    /// <summary>JSONPath to the array of WeClapp <c>tax</c> entities the pipeline fetched. A sales
    /// order position states its net, its gross and a <c>taxId</c> but no percentage - the rate
    /// lives on that separate entity, the way the AS delivery's purchase prices live on
    /// <c>articleSupplySource</c>. Required: without the set every position would render an empty
    /// MwSt field, and empty is the legitimate value for a position that states no tax, so the
    /// defect would be invisible in the delivered file.</summary>
    public string TaxesPath { get; set; } = "";
}

/// <summary>
/// Renders a WeClapp sales order from the pipeline data context into DILOS AI file content.
/// What is left here now that the AS article master renders through the product's column node is
/// the part that is genuinely custom: mixed K*/P* record types in one file, a synthesised
/// shipping line and a position counter. None of that is column rendering.
///
/// It also joins the fetched WeClapp <c>tax</c> entities, because the positions state a VAT rate
/// that no order payload carries - the position names a tax entity and the percentage lives there.
/// That join sits here rather than in a node of its own (as the AS side's EK-Preis does, in
/// WeClappResolveSupplySources@1) because this chain HAS an adapter-owned render step to put it in,
/// while the AS chain renders through the product's generic column node, which can only read a path.
/// </summary>
[NodeConfiguration(typeof(DilosRenderNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosRenderNode(NodeDelegate next) : IPipelineNode
{
    private const string NodeName = "DilosRender";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>();

        if (config.Mode != "AI")
        {
            throw new WeClappPipelineExecutionException(
                $"Unknown DilosRender mode '{config.Mode}' (expected 'AI'; the AS article master " +
                "renders through RenderDelimitedText@1)");
        }

        // Both paths are checked before anything is read or written, for the reason the shared
        // guard spells out - and here inside a per-order ForEach@1 carrying continueOnError, where
        // an unattributed failure is booked as a failed order rather than a configuration defect.
        NodeConfigurationGuards.RequirePath(NodeName, config.Path, nameof(config.Path));
        NodeConfigurationGuards.RequirePath(NodeName, config.TargetPath, nameof(config.TargetPath));
        NodeConfigurationGuards.RequirePath(NodeName, config.TaxesPath, nameof(config.TaxesPath));

        var orders = ReadOneOrMany<WeClappSalesOrder>(dataContext, config.Path, "order");
        var content = RenderOrders(orders, config, ReadTaxValues(dataContext, config.TaxesPath));

        // IsNullOrEmpty rather than .Length: the properties are non-nullable, but a yaml
        // carrying an explicit null ("fileNameTargetPath:" with no value) assigns null OVER the
        // initializer, and a bare dereference here fails as an unattributable
        // NullReferenceException inside the per-order loop - which continueOnError then
        // swallows as one failed order instead of the configuration defect it is.
        var fileName = !string.IsNullOrEmpty(config.FileNameTargetPath)
            ? EnsurePlainFileName(BuildAiFileName(orders))
            : "";

        // Empty content must never reach the delivery: SftpUpload@1 uploads "" as a 0-byte file
        // rather than refusing it, and the export marker is written after the upload. An AI
        // execution always renders at least its K* header, so empty content here is an upstream
        // defect and fails loudly. The AS side reaches the same end by a different route - its
        // renderer writes the empty string and the yaml gates the delivery on it.
        if (content.Length == 0)
        {
            throw new WeClappPipelineExecutionException(
                "DilosRender rendered no content - refusing to deliver an empty DILOS file");
        }

        dataContext.Set(config.TargetPath, content, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        if (fileName.Length > 0)
        {
            dataContext.Set(config.FileNameTargetPath, fileName, config.DocumentMode,
                ValueKinds.Simple, TargetValueWriteModes.Overwrite);
        }

        nodeContext.Info("DilosRender: rendered AI content ({0} chars){1}",
            content.Length, fileName.Length > 0 ? $" as '{fileName}'" : "");

        await next(dataContext, nodeContext);
    }

    private static string RenderOrders(List<WeClappSalesOrder> orders, DilosRenderNodeConfiguration config,
        IReadOnlyDictionary<string, string?> taxValueById)
    {
        if (string.IsNullOrEmpty(config.Submandant))
        {
            throw new WeClappPipelineExecutionException("DilosRender mode 'AI' requires Submandant");
        }

        var ctx = new DilosOrderContext
        {
            Submandant = config.Submandant,
            TaxValueById = taxValueById,
        };
        var lines = orders
            .SelectMany(o => new[] { DilosOrderWriter.RenderHeader(o, ctx) }
                .Concat(DilosOrderWriter.RenderPositions(o, ctx)));

        // The writer refuses an unreachable or unreadable tax rate and a missing or unreadable
        // amount, and the render is lazy, so those refusals surface here, from inside the join.
        // Caught by TYPE rather than as "anything that escaped": this runs inside a per-order
        // ForEach@1 carrying continueOnError, which books whatever arrives as "one order failed",
        // and a catch-all would let a future NullReferenceException turn up wearing the message of
        // a designed data rejection. An InvalidOperationException from the writer IS that
        // rejection; everything else propagates as itself.
        try
        {
            return JoinLf(lines);
        }
        catch (InvalidOperationException ex)
        {
            throw new WeClappPipelineExecutionException($"DilosRender: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Indexes the fetched WeClapp tax entities by id, carrying the <c>taxValue</c> across
    /// UNPARSED. Reading a rate here would validate the whole account's tax list on every
    /// execution, so one unusable entity - in a set of 235, of which a typical order references
    /// three - would fail every order until someone repaired a record none of them are taxed
    /// under. The rate is therefore parsed and range-checked at the position that names it, in
    /// DilosOrderWriter. What DOES belong here is what makes the set usable at all: an array at
    /// the path, an id per entity, and no id twice - the last of which is also the paging
    /// cross-check, since overlapping pages deliver the same entity again.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ReadTaxValues(IDataContext dataContext, string path)
    {
        var taxes = dataContext.GetArray<WeClappTax>(path)?.ToList()
                    ?? throw new WeClappPipelineExecutionException(
                        $"DilosRender: no WeClapp tax array found at path '{path}' - every position " +
                        "would render an empty MwSt rate, which is a legitimate value and therefore " +
                        "invisible in the delivered file");

        return NodeElementIndex
            .ById(taxes, tax => tax.Id, NodeName, path, "tax",
                "the order positions taxed under it resolve against exactly that value")
            .ToDictionary(entry => entry.Key, entry => entry.Value.TaxValue, StringComparer.Ordinal);
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
                "Order has no id (Auftragsnummer1) - cannot build the AI file name");
        }

        return DilosFile.AiFileName(auftragsnummer1);
    }

    /// <summary>A DILOS file name is a bare name, never a path. The AI name carries the
    /// external WeClapp order number, so a poisoned value would otherwise travel into the
    /// delivery node - which resolves such a name to its last segment and uploads under that
    /// name without complaining. Rejecting here is loud and retried on the next tick.</summary>
    private static string EnsurePlainFileName(string fileName)
    {
        if (!DilosFile.IsPlainFileName(fileName))
        {
            throw new WeClappPipelineExecutionException(
                $"DILOS file name '{fileName}' contains a path separator or dot segment - refusing to deliver");
        }

        return fileName;
    }

    /// <summary>Reads the source as an array OR a single object - per-document pipelines
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
