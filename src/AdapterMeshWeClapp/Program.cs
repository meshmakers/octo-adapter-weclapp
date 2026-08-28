using Meshmakers.Octo.Communication.MeshAdapter.WeClapp;
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
    // The DEFAULT client is registered alongside the named ones: the standard
    // MakeHttpRequest@1 node resolves a plain injected HttpClient, which is that default.
    builder.Services.AddWeClappHttpClients();

    // SFTP seam for the DILOS AR/BE return path - the remote delete both DilosFileGate@1 (settling
    // a delete an earlier tick still owed) and DilosFileConfirm@1 (the keep/delete decision after a
    // successful write) perform. SSH.NET-backed in production, faked in tests. The listing and the
    // download themselves are the product's SftpList@1 / SftpDownload@1 and never reach this seam.
    builder.Services.AddSingleton<ISftpFileSystemFactory, SshNetSftpFileSystemFactory>();

    // Cross-tick memory for DilosFileGate@1 / DilosFileConfirm@1 (AR/BE return path): the pipeline
    // engine constructs a fresh node instance per chain, i.e. per tick, so which files an earlier
    // tick already processed cannot live on the nodes themselves. ONE instance is shared by the ar
    // AND the be pipeline, which is why every key carries a scope prefix. A pod restart clears it;
    // a pipeline redeploy does not.
    builder.Services.AddSingleton<DilosFileFetchState>();

    // Add the adapter's own nodes to the container. Outbound: DilosExportRunKey@1 →
    // WeClappResolveSupplySources@1 → WeClappToCk@1 / DilosRender@1 (AI only — the AS
    // article master renders through the product's RenderDelimitedText@1), with the product's
    // MakeHttpRequest@1 fetching and SftpUpload@1 delivering. Return path: DilosFileGate@1 →
    // WeClappArWrite@1 / WeClappBeWrite@1 → DilosFileConfirm@1, with the product's SftpList@1 and
    // SftpDownload@1 doing the SFTP mechanics. Every pipeline is driven by a passive trigger from
    // the product, so this adapter declares no trigger node of its own.
    builder.Services.AddOctoMeshAdapter()
        .RegisterNode<DilosExportRunKeyNode>()
        .RegisterNode<WeClappResolveSupplySourcesNode>()
        .RegisterNode<WeClappToCkNode>()
        .RegisterNode<DilosRenderNode>()
        .RegisterNode<DilosFileGateNode>()
        .RegisterNode<WeClappArWriteNode>()
        .RegisterNode<WeClappBeWriteNode>()
        .RegisterNode<DilosFileConfirmNode>();

}, app =>
{
    app.MapObservability();
    app.UseOctoMeshAdapter();
});
