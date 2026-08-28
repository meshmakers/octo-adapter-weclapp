using System.Globalization;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core;
using Lkv.WeClapp.Core.Dilos;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosExportRunKey node: writes the key of today's DILOS export run to
/// <c>TargetPath</c> as <c>{ exportKind, exportDay, fileName }</c> - the first two key the daily
/// marker, the third names the delivered file.
/// </summary>
[NodeName("DilosExportRunKey", 1)]
public record DilosExportRunKeyNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Kind of delivery this run belongs to, e.g. "AS". Together with the day it is the
    /// key of the Industry.Logistics/ExportRun marker that limits the delivery to one per
    /// calendar day.</summary>
    public required string ExportKind { get; set; }
}

/// <summary>
/// Writes the export-run key of the current Austrian calendar day, plus the name the delivery
/// file gets. The day is taken in Vienna time rather than UTC: the delivery it gates is one file
/// per calendar day in the customer's calendar, and between midnight and 01:00 or 02:00 local the
/// two disagree.
///
/// The file name is built from the SAME clock read as the day, which is the point of putting it
/// here rather than at the render: two reads can straddle Vienna midnight, and the delivery would
/// then carry the name of day N+1 under the marker of day N - with no marker for N+1 yet, the next
/// tick delivers that day a second time. One read makes that unrepresentable.
///
/// This node is a stand-in for a capability the platform does not have yet. Everything else it
/// does is a constant and a date format, both of which the standard nodes cover; only the time
/// zone does not exist on <c>DateTime@1</c>. Once it does, this node goes away.
/// </summary>
[NodeConfiguration(typeof(DilosExportRunKeyNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosExportRunKeyNode(NodeDelegate next, TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosExportRunKeyNodeConfiguration>();

        // Guards first: a key with a blank kind would match every marker of every other kind,
        // and a blank target path would leave the probe reading null and the gate shut.
        if (string.IsNullOrWhiteSpace(config.ExportKind))
        {
            throw new WeClappPipelineExecutionException(
                "DilosExportRunKey: 'ExportKind' must name the delivery kind, e.g. 'AS'");
        }

        if (string.IsNullOrWhiteSpace(config.TargetPath))
        {
            throw new WeClappPipelineExecutionException(
                "DilosExportRunKey: 'TargetPath' must be a JSONPath");
        }

        // ONE clock read feeds both values - see the type summary for the midnight window that
        // closes. Everything below derives from utcNow; nothing may read the clock again.
        var utcNow = _timeProvider.GetUtcNow();
        var viennaNow = TimeZoneInfo.ConvertTime(utcNow, ViennaTime.Zone);
        var fileName = DilosFile.DeliveryFileName(config.ExportKind, utcNow);

        // The name is machine-generated, so this is theoretical - but the kind comes from the
        // pipeline definition, and SftpUpload@1 resolves a name carrying path segments to its
        // last segment and delivers under that instead of refusing it.
        if (!DilosFile.IsPlainFileName(fileName))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosExportRunKey: delivery file name '{fileName}' contains a path separator or " +
                "dot segment - refusing to deliver");
        }

        var key = new JsonObject
        {
            ["exportKind"] = config.ExportKind,
            ["exportDay"] = viennaNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["fileName"] = fileName,
        };

        dataContext.Set<JsonNode>(config.TargetPath, key, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }
}
