using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the DilosFileGate transform node — the state gate between the product's
/// <c>SftpList@1</c> listing and the per-file <c>ForEach@1</c>.
/// </summary>
[NodeName("DilosFileGate", 1)]
public record DilosFileGateNodeConfiguration : NodeConfiguration
{
    /// <summary>Delete the remote file once its downstream processing succeeded. This is the ONE
    /// place the mode is configured: the gate stamps it into every element it lets through, and
    /// <c>DilosFileConfirm@1</c> reads it from there, so the two can no longer disagree.</summary>
    public bool DeleteAfterSuccess { get; set; }

    /// <summary>Data context path of the listed array to gate — the <c>targetPath</c> of the
    /// feeding <c>SftpList@1</c>. The filtered array is written back to the same path. It has to
    /// be configurable because the listing node's target path is: a gate hard-coded to
    /// <c>$.files</c> would silently do nothing against a listing that went somewhere else.</summary>
    public string Path { get; set; } = "$.files";
}

/// <summary>
/// Filters and stamps the array <c>SftpList@1</c> emitted, so the per-file chain behind it sees
/// only the files that still need work. For each listed element the gate stamps the scoped file
/// key plus the keep/delete mode and the server the file came from, and lets the element
/// through.
/// <para/>
/// The scope the state is keyed by comes from the element's own <c>source</c> object, not from
/// this node's configuration: the three values that identify a listing (server entry, directory,
/// pattern) are already configured once, on the listing node, and re-declaring them here would
/// reintroduce the two-places-one-value problem the mode stamp removes.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="logger">Logger</param>
/// <param name="etlContext">The ETL context carrying the tenant global configuration</param>
/// <param name="sftpFileSystemFactory">Opens the SFTP session a delete retry needs</param>
/// <param name="state">Cross-tick memory shared with <c>DilosFileConfirm@1</c></param>
[NodeConfiguration(typeof(DilosFileGateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DilosFileGateNode(
    NodeDelegate next,
    ILogger<DilosFileGateNode> logger,
    IMeshEtlContext etlContext,
    ISftpFileSystemFactory sftpFileSystemFactory,
    DilosFileFetchState state) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<DilosFileGateNodeConfiguration>();

        // A dry-run leaves no trace: no remote deletes and no cross-tick state writes - only
        // the read-and-emit surface runs, so the chain behind the gate can be probed safely.
        var isDryRun = nodeContext.PipelineExecutionMode?.IsDryRun == true;

        // An empty listing is normal; a path that holds nothing at all is a wiring error. Left
        // to the null fallback the gate would write an empty array and every tick would run
        // green while the files pile up on the LKV server - and the array it writes is what
        // hides it, because a downstream ForEach@1 would otherwise abort on the missing path.
        if (!dataContext.Exists(config.Path))
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileGate: nothing at '{config.Path}' - the path must name the targetPath of " +
                "the SftpList@1 that feeds this gate");
        }

        var listed = dataContext.Get<JsonArray>(config.Path) ?? new JsonArray();
        var files = listed.OfType<JsonObject>().Select(Identify).ToList();

        // Forget the keys of files that vanished from the server, scope by scope, BEFORE the
        // gating below reads them - the way the listing step this replaces did. Scoped, because
        // the singleton is shared with the other pipeline: an unscoped prune would drop the
        // marks of every file this listing naturally never mentions.
        if (!isDryRun)
        {
            foreach (var scope in files.GroupBy(f => f.ScopePrefix, StringComparer.Ordinal))
            {
                state.PruneScopeTo(scope.Key, scope.Select(f => f.Key));
            }
        }

        var gated = new JsonArray();

        foreach (var (element, source, key, _) in files)
        {
            // Gated on the CURRENT mode: after a flip the singleton still holds the other
            // mode's marks, and a stale keep mark must not suppress a real emission.
            if (!config.DeleteAfterSuccess && state.WasKeptOnServer(key))
            {
                continue; // keep mode: already confirmed, stays on the server unchanged
            }

            if (config.DeleteAfterSuccess && state.HasPendingDelete(key))
            {
                var fileName = element["name"]!.GetValue<string>();
                if (isDryRun)
                {
                    nodeContext.Info("DilosFileGate dry-run: would retry the delete of '{0}'", fileName);
                    continue;
                }

                try
                {
                    // The session is opened per retry, the way DilosFileConfirm@1 opens one per
                    // delete: a retry is the exception, not the rhythm of a tick.
                    var settings = etlContext.GlobalConfiguration.ResolveSftpSettings(source);
                    using (var sftp = sftpFileSystemFactory.Connect(settings))
                    {
                        sftp.DeleteFile(Required<string>(element, "fullPath"));
                    }

                    state.ClearPendingDelete(key);
                }
                catch (Exception ex)
                {
                    // A remote entry that refuses to be deleted is a property of that entry, not
                    // of the tick: the key stays pending for the next one and the files behind
                    // this one still reach the per-file chain.
                    logger.LogError(ex, "DilosFileGate: retrying the delete of '{FileName}' failed", fileName);
                    nodeContext.Error("DilosFileGate: retrying the delete of '{0}' failed: {1}", fileName,
                        ex.Message);
                }

                continue; // never re-emitted, never re-processed - only the delete was owed
            }

            // DeepClone detaches the element from the array it was read out of: a JsonNode
            // belongs to exactly one parent, so the stamped copy is what travels on.
            var stamped = element.DeepClone().AsObject();
            stamped["key"] = key;
            stamped["deleteAfterSuccess"] = config.DeleteAfterSuccess;
            stamped["serverConfiguration"] = source;
            gated.Add(stamped);
        }

        dataContext.Set(config.Path, gated, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        logger.LogDebug("DilosFileGate: {Count} of {Total} listed file(s) passed the gate",
            gated.Count, listed.Count);

        await next(dataContext, nodeContext);
    }

    /// <summary>Reads the three scope values and the file identity off ONE listed element and
    /// derives the key the cross-tick state is kept under.</summary>
    private static (JsonObject Element, string Source, string Key, string ScopePrefix) Identify(JsonObject element)
    {
        var serverConfiguration = Required<string>(element, "source.serverConfiguration");
        var scopePrefix = DilosFileFetchCore.ScopePrefix(
            serverConfiguration,
            Required<string>(element, "source.remoteDirectory"),
            Required<string>(element, "source.filePattern"));
        var key = scopePrefix + DilosFileFetchCore.FileKey(
            Required<string>(element, "name"),
            Required<long>(element, "length"),
            Required<string>(element, "lastWriteTimeUtc"));

        return (element, serverConfiguration, key, scopePrefix);
    }

    /// <summary>Reads one value the gate cannot work without and names the field when it is
    /// absent. Every one of them comes from <c>SftpList@1</c>, so a missing field means the
    /// configured path points at something else - reported as such rather than as a null
    /// reference from somewhere inside the key composition.</summary>
    private static T Required<T>(JsonObject element, string field)
    {
        JsonNode? node = element;
        foreach (var segment in field.Split('.'))
        {
            node = node?[segment];
        }

        if (node is null)
        {
            throw new WeClappPipelineExecutionException(
                $"DilosFileGate: listed file element has no '{field}' - the gate reads the files " +
                "SftpList@1 emitted, so its path must name that node's targetPath");
        }

        return node.GetValue<T>();
    }
}
