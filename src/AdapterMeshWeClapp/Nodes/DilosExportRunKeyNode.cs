using System.Globalization;
using System.Text.Json.Nodes;
using Lkv.WeClapp.Core;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosExportRunKey node: writes the key of today's DILOS export run to
/// <c>TargetPath</c> as <c>{ exportKind, exportDay }</c>.
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
/// Writes the export-run key of the current Austrian calendar day. The day is taken in Vienna
/// time rather than UTC: the delivery it gates is one file per calendar day in the customer's
/// calendar, and between midnight and 01:00 or 02:00 local the two disagree.
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

        var viennaNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), ViennaTime.Zone);
        var key = new JsonObject
        {
            ["exportKind"] = config.ExportKind,
            ["exportDay"] = viennaNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        dataContext.Set<JsonNode>(config.TargetPath, key, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }
}
