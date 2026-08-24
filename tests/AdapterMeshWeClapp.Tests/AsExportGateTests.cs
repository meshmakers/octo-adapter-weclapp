using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Export dedup for the AS delivery pipeline. Unlike the AI gate (where the CK order entity
/// itself is the marker), the AS batch has no per-order entity to key on — the whole
/// { items, meta } snapshot is one delivery per Vienna calendar day. A dedicated marker
/// entity (Industry.Logistics/ExportRun) keyed on meta.exportKind + meta.exportDate carries
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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline()
            .AddMeshDataPipelineNodes()          // GetOrCreate/CreateUpdateInfo/ApplyChanges/…
            .RegisterNodeConfiguration<IfNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchTriggerNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappToCkNodeConfiguration>()
            .RegisterNodeConfiguration<DilosRenderNodeConfiguration>()
            .RegisterNodeConfiguration<WeClappFetchStepNodeConfiguration>();
        var lookup = services.BuildServiceProvider().GetRequiredService<INodeQualifiedNameLookupService>();

        NodeDefinitionRoot root;
        await using (var stream = File.OpenRead(FindRepoFile(Path.Combine("pipelines", "weclapp-articles-to-as.yaml"))))
        {
            root = await new YamlPipelineConfigurationSerializer(lookup).DeserializeAsync(stream)
                   ?? throw new InvalidOperationException("pipeline yaml deserialized to null");
        }

        // Trigger-Pins: passive cron pair. K2 anti-starvation is now structural — a
        // FromPipelineTriggerEvent@1 trigger never fires on (re)deploy by design, so there is
        // no RunOnStart/PollingIntervalSeconds config left to get wrong (AB#4228 trigger
        // separation). The K1 prerequisites move to the fetch-step pins below.
        Assert.Collection(root.Triggers!,
            t => Assert.IsType<FromPipelineTriggerEventNodeConfiguration>(t),
            t => Assert.IsType<FromExecutePipelineCommandNodeConfiguration>(t));

        var top = root.Transformations?.ToList() ?? new List<NodeConfiguration>();

        // Fetch-step pins: Batch/AS feed $.meta.exportKind/$.meta.exportDate that the gate below reads.
        var fetchStep = Assert.Single(top.OfType<WeClappFetchStepNodeConfiguration>());
        Assert.Equal("article", fetchStep.Entity);
        Assert.Equal("Batch", fetchStep.EmitMode);
        Assert.Equal("AS", fetchStep.ExportKind);

        // Lookup (query-only) außerhalb des Gates, nichts Lieferndes/Persistierendes davor:
        var probe = Assert.Single(top.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
        Assert.Equal("Industry.Logistics/ExportRun", probe.CkTypeId);
        Assert.NotNull(probe.FieldFilters);
        Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportKind");
        Assert.Contains(probe.FieldFilters!, f => f.ComparisonValuePath == "$.meta.exportDate");
        Assert.DoesNotContain(top, n => n is DilosRenderNodeConfiguration);
        Assert.DoesNotContain(top, n => n is SftpUploadNodeConfiguration);
        Assert.DoesNotContain(top, n => n is ApplyChangesNodeConfiguration2);
        Assert.DoesNotContain(top, n => n is CreateUpdateInfoNodeConfiguration);

        // Ein Gate mit den testbewiesenen Literalen (Insert = heute noch nicht geliefert):
        var gate = Assert.Single(top.OfType<IfNodeConfiguration>());
        Assert.Equal("$.rt.asExportRunModOperation", gate.Path);
        Assert.Equal(CompareOperator.Equal, gate.Operator);
        Assert.Equal(AttributeValueTypesDto.Enum, gate.ValueType);
        Assert.Equal((int)UpdateKind.Insert, Convert.ToInt32(gate.Value));

        // Reihenfolge auf Top-Ebene: die query-only Probe läuft VOR dem liefernden Gate:
        var probeIndex = top.FindIndex(n => n is GetOrCreateRtEntitiesByTypeNodeConfiguration);
        var gateIndex = top.FindIndex(n => n is IfNodeConfiguration);
        Assert.True(probeIndex < gateIndex, "probe must run before the gate");

        // Im Gate: render → upload → Marker-CreateUpdateInfo → ApplyChanges@2 als LETZTER Schritt:
        var children = gate.Transformations!.ToList();
        var renderIndex = children.FindIndex(n => n is DilosRenderNodeConfiguration);
        var uploadIndex = children.FindIndex(n => n is SftpUploadNodeConfiguration);
        var markerIndex = children.FindIndex(n => n is CreateUpdateInfoNodeConfiguration);
        var persistIndex = children.FindIndex(n => n is ApplyChangesNodeConfiguration2);
        Assert.True(renderIndex >= 0);
        Assert.True(uploadIndex > renderIndex, "Upload nach Render, im Gate");
        Assert.True(markerIndex > uploadIndex, "Marker-Update NACH dem Upload (at-least-once)");
        Assert.Equal(persistIndex, children.Count - 1);

        // Pfad-VERDRAHTUNG: jede dieser String-Verbindungen kann per YAML-Edit einseitig
        // brechen, ohne dass Struktur-/Reihenfolge-Pins rot werden — zur Laufzeit wäre das
        // ein STILLER Fehler (Gate liest null ⇒ dauer-zu = Liefer-Starvation; ApplyChanges
        // findet keine Updates ⇒ nur Warning, Marker persistiert nie ⇒ Gate dauer-offen;
        // Marker-Werte matchen die Probe nie ⇒ tägliche Duplikate).
        Assert.Equal("$.rt.asExportRunModOperation", probe.ModOperationPath);
        Assert.Equal(probe.ModOperationPath, gate.Path);
        Assert.Equal("$.rt.asExportRunRtId", probe.RtIdTargetPath);

        var marker = Assert.Single(children.OfType<CreateUpdateInfoNodeConfiguration>());
        Assert.Equal(probe.RtIdTargetPath, marker.RtIdPath);
        Assert.Equal(probe.ModOperationPath, marker.UpdateKindPath);
        Assert.Equal(probe.CkTypeId, marker.CkTypeId);

        var persist = Assert.Single(children.OfType<ApplyChangesNodeConfiguration2>());
        Assert.Equal(marker.TargetPath, persist.EntityUpdatesPath);

        // Der Marker schreibt exakt die Attribute/Pfade, auf die die Probe filtert:
        Assert.Contains(probe.FieldFilters!, f => f.AttributePath == "ExportKind" && f.ComparisonValuePath == "$.meta.exportKind");
        Assert.Contains(probe.FieldFilters!, f => f.AttributePath == "ExportDate" && f.ComparisonValuePath == "$.meta.exportDate");
        Assert.NotNull(marker.AttributeUpdates);
        Assert.Contains(marker.AttributeUpdates!, u => u.AttributeName == "ExportKind" && u.ValuePath == "$.meta.exportKind");
        Assert.Contains(marker.AttributeUpdates!, u => u.AttributeName == "ExportDate" && u.ValuePath == "$.meta.exportDate");
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
