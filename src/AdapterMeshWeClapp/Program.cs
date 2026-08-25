using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Services.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// WebAdapterBuilder is a builder for creating Adapters acting as a Socket (Listener) or a Web API (Host)
var adapterBuilder = new WebAdapterBuilder();

await adapterBuilder.RunAsync(args, builder =>
{
    // Define the configuration for the adapter
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.Configure<MeshAdapterConfiguration>(options =>
        builder.Configuration.GetSection("Adapter").Bind(options));

    // Observability: health checks + the HTTP endpoints the Helm chart probes
    // (/healthz/live, /healthz/ready) — mapped by MapObservability below.
    builder.AddObservability()
        .AddSystemContextHealthCheck();

    // Add the adapter service to startup and shutdown the adapter
    builder.Services.AddSingleton<IAdapterService, AdapterMeshWeClappService>();

    // WeClapp serves gzip-compressed responses (live-verified: raw bodies start with 0x1F) —
    // every named WeClapp client must decompress automatically.
    foreach (var clientName in new[]
             {
                 nameof(WeClappFetchTriggerNode), nameof(WeClappArWriteNode), nameof(WeClappBeWriteNode),
             })
    {
        builder.Services.AddHttpClient(clientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
    }

    // SFTP seam for the DILOS AR/BE return path — shared by the legacy DilosFileFetch trigger
    // AND its cron-trigger replacements DilosFileFetchStep@1/DilosFileConfirm@1 — SSH.NET-backed
    // in production, faked in tests.
    builder.Services.AddSingleton<ISftpFileSystemFactory, SshNetSftpFileSystemFactory>();

    // Cross-tick memory for DilosFileFetchStep@1 / DilosFileConfirm@1 (AR/BE return path,
    // AB#4228/G2 cron-trigger redesign) — nodes are constructed fresh per pipeline chain (per
    // tick), so the poll-loop instance fields the legacy DilosFileFetch trigger relies on would
    // lose their state between ticks; this singleton replaces them.
    builder.Services.AddSingleton<DilosFileFetchState>();

    // Add mesh adapter nodes and services to the container:
    // outbound ingestion design (WeClappFetch → WeClappToCk → DilosRender → the product's
    // SftpUpload@1) plus the AR/BE return path (DilosFileFetch → WeClappArWrite /
    // WeClappBeWrite) — both still registered as the legacy poll-loop triggers, alongside their
    // cron-trigger counterparts WeClappFetchStep@1 and DilosFileFetchStep@1/DilosFileConfirm@1
    // (the latter pair sharing cross-tick state through the DilosFileFetchState singleton above).
    builder.Services.AddOctoMeshAdapter()
        .RegisterTriggerNode<WeClappFetchTriggerNode>()
        .RegisterTriggerNode<DilosFileFetchTriggerNode>()
        .RegisterNode<WeClappToCkNode>()
        .RegisterNode<DilosRenderNode>()
        .RegisterNode<WeClappArWriteNode>()
        .RegisterNode<WeClappBeWriteNode>()
        .RegisterNode<WeClappFetchStepNode>()
        .RegisterNode<DilosFileFetchStepNode>()
        .RegisterNode<DilosFileConfirmNode>();

}, app =>
{
    app.MapObservability();
    app.UseOctoMeshAdapter();
});
