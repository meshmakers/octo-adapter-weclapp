using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Export dedup for the AS delivery pipeline. Unlike the AI gate (where the CK order entity
/// itself is the marker), the AS batch has no per-order entity to key on — the whole
/// { items, meta } snapshot is one delivery per Vienna calendar day. A dedicated marker
/// entity (Industry.Logistics/ExportRun) keyed on meta.exportKind + meta.exportDay carries
/// the "already delivered today" fact: the GetOrCreate probe QUERIES it outside the gate, the
/// If@1 delivers only on a miss (ModOperation Insert), and ApplyChanges@2 persists the marker
/// as the LAST step — only after a successful upload (at-least-once).
///
/// The If@1 literals (Path/Operator/ValueType/Value = Insert) are the SAME ones the
/// AiExportGateTests semantics tests prove against a production DataContext write; only the
/// ModOperation path differs and is pinned here through the shipped YAML.
/// </summary>
public class AsExportGateTests
{
    [Fact]
    public async Task ArticlesToAsYaml_GatesDeliveryOnDailyMarker_AndPersistsOnlyAfterUpload()
    {
        var root = await PipelineDefinitions.DeserializeAsync("weclapp-articles-to-as.yaml");

        // Trigger pins: passive cron pair. K2 anti-starvation is now structural — a
        // FromPipelineTriggerEvent@1 trigger never fires on (re)deploy by design, so there is
        // no RunOnStart/PollingIntervalSeconds config left to get wrong (AB#4228 trigger
        // separation). The K1 prerequisites move to the export-run key pins below.
        Assert.Collection(root.Triggers!,
            t => Assert.IsType<FromPipelineTriggerEventNodeConfiguration>(t),
            t => Assert.IsType<FromExecutePipelineCommandNodeConfiguration>(t));

        var top = root.Transformations?.ToList() ?? new List<NodeConfiguration>();

        // The export-run key is written BEFORE the probe and is the only thing the probe needs:
        // the whole fetch now sits inside the gate, so a day that was already delivered costs
        // no WeClapp request at all.
        var exportRunKey = Assert.Single(top.OfType<DilosExportRunKeyNodeConfiguration>());
        Assert.Equal("AS", exportRunKey.ExportKind);
        Assert.Equal("$.meta", exportRunKey.TargetPath);
        Assert.DoesNotContain(top, n => n is MakeHttpRequestNodeConfiguration);
        Assert.DoesNotContain(top, n => n is WeClappResolveSupplySourcesNodeConfiguration);

        // The lookup (query only) sits outside the gate, and nothing that delivers or persists
        // may stand before it:
        var probe = Assert.Single(top.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
        Assert.Equal("Industry.Logistics/ExportRun", probe.CkTypeId);
        Assert.NotNull(probe.FieldFilters);
        Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportKind");
        Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportDay");
        Assert.DoesNotContain(top, n => n is RenderDelimitedTextNodeConfiguration);
        Assert.DoesNotContain(top, n => n is SftpUploadNodeConfiguration);
        Assert.DoesNotContain(top, n => n is ApplyChangesNodeConfiguration2);
        Assert.DoesNotContain(top, n => n is CreateUpdateInfoNodeConfiguration);

        // One gate, carrying the literals the semantics tests prove (Insert = not delivered today):
        var gate = Assert.Single(top.OfType<IfNodeConfiguration>());
        Assert.Equal("$.rt.asExportRunModOperation", gate.Path);
        Assert.Equal(CompareOperator.Equal, gate.Operator);
        Assert.Equal(AttributeValueTypesDto.Enum, gate.ValueType);
        Assert.Equal((int)UpdateKind.Insert, Convert.ToInt32(gate.Value));

        // Top-level order: the query-only probe runs BEFORE the gate that delivers.
        var probeIndex = top.FindIndex(n => n is GetOrCreateRtEntitiesByTypeNodeConfiguration);
        var gateIndex = top.FindIndex(n => n is IfNodeConfiguration);
        Assert.True(probeIndex < gateIndex, "probe must run before the gate");

        // Inside the gate: render, then the empty-batch brake, and INSIDE that one upload ->
        // marker -> ApplyChanges@2 as the LAST step. Two levels deep, and that the nesting holds
        // is a claim of its own: the daily gate must deliver nothing when the batch rendered
        // empty, and the marker must not persist before a successful upload.
        var children = gate.Transformations!.ToList();
        var renderIndex = children.FindIndex(n => n is RenderDelimitedTextNodeConfiguration);
        var contentGateIndex = children.FindIndex(n => n is IfNodeConfiguration);
        Assert.True(renderIndex >= 0);
        Assert.True(contentGateIndex > renderIndex, "the empty-batch brake stands behind the render");
        Assert.Equal(children.Count - 1, contentGateIndex);

        var contentGate = Assert.Single(children.OfType<IfNodeConfiguration>());
        var delivery = contentGate.Transformations!.ToList();
        var uploadIndex = delivery.FindIndex(n => n is SftpUploadNodeConfiguration);
        var markerIndex = delivery.FindIndex(n => n is CreateUpdateInfoNodeConfiguration);
        var persistIndex = delivery.FindIndex(n => n is ApplyChangesNodeConfiguration2);
        Assert.True(uploadIndex >= 0, "the upload sits inside the empty-batch brake");
        Assert.True(markerIndex > uploadIndex, "marker update AFTER the upload (at-least-once)");
        Assert.Equal(persistIndex, delivery.Count - 1);

        // Inside the gate the order is: articles -> supply sources -> enrichment -> render.
        // The enrichment must sit between the two fetches and the render, or the EK-Preis column
        // silently becomes 0 for every article while the file still looks complete.
        var fetchIndexes = children
            .Select((n, i) => (Node: n, Index: i))
            .Where(x => x.Node is MakeHttpRequestNodeConfiguration)
            .Select(x => x.Index)
            .ToList();
        var enrichIndex = children.FindIndex(n => n is WeClappResolveSupplySourcesNodeConfiguration);
        Assert.Equal(2, fetchIndexes.Count);
        Assert.All(fetchIndexes, index => Assert.True(index < enrichIndex));
        Assert.True(enrichIndex < renderIndex, "enrichment runs before the render");

        var enrich = Assert.Single(children.OfType<WeClappResolveSupplySourcesNodeConfiguration>());
        var render = Assert.Single(children.OfType<RenderDelimitedTextNodeConfiguration>());
        Assert.Equal(enrich.TargetPath, render.Path);

        // …and each fetch must hand its array to the path the enrichment READS. Order alone does
        // not establish that: renaming one targetPath (or one of the enrichment's two source
        // paths) leaves every structural pin above green and every offline fixture unaffected,
        // because the fixtures seed the paths themselves. At the tenant the first tick after the
        // deploy fails with "no article array at path '…'" and the AS delivery is dead until
        // someone reads the failed executions. The mirror-image rename on the AR/BE return path
        // is pinned (ForEach.iterationPath == SftpList.targetPath); this is the same pin.
        var fetches = children.OfType<MakeHttpRequestNodeConfiguration>().ToList();
        var articleFetch = Assert.Single(fetches, f => f.Url == "/article");
        var priceFetch = Assert.Single(fetches, f => f.Url == "/articleSupplySource");
        Assert.Equal(articleFetch.TargetPath, enrich.Path);
        Assert.Equal(priceFetch.TargetPath, enrich.SupplySourcesPath);

        // Path WIRING: every one of these string connections can be broken on ONE side by a yaml
        // edit without any structural or ordering pin going red — at runtime that would be a
        // SILENT fault (the gate reads null => permanently closed = delivery starvation;
        // ApplyChanges finds no updates => warning only, the marker never persists => the gate
        // stays permanently open; marker values that never match the probe => daily duplicates).
        Assert.Equal("$.rt.asExportRunModOperation", probe.ModOperationPath);
        Assert.Equal(probe.ModOperationPath, gate.Path);
        Assert.Equal("$.rt.asExportRunRtId", probe.RtIdTargetPath);

        var marker = Assert.Single(delivery.OfType<CreateUpdateInfoNodeConfiguration>());
        Assert.Equal(probe.RtIdTargetPath, marker.RtIdPath);
        Assert.Equal(probe.ModOperationPath, marker.UpdateKindPath);
        Assert.Equal(probe.CkTypeId, marker.CkTypeId);

        var persist = Assert.Single(delivery.OfType<ApplyChangesNodeConfiguration2>());
        Assert.Equal(marker.TargetPath, persist.EntityUpdatesPath);

        // The marker writes exactly the attributes and paths the probe filters on:
        Assert.Contains(probe.FieldFilters!, f => f.AttributePath == "ExportKind" && f.ComparisonValuePath == "$.meta.exportKind");
        Assert.Contains(probe.FieldFilters!, f => f.AttributePath == "ExportDay" && f.ComparisonValuePath == "$.meta.exportDay");
        Assert.NotNull(marker.AttributeUpdates);
        Assert.Contains(marker.AttributeUpdates!, u => u.AttributeName == "ExportKind" && u.ValuePath == "$.meta.exportKind");
        Assert.Contains(marker.AttributeUpdates!, u => u.AttributeName == "ExportDay" && u.ValuePath == "$.meta.exportDay");
    }
}
