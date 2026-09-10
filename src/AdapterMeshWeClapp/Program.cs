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

    // Add the adapter's own nodes to the container. Outbound: WeClappResolveSupplySources@1 →
    // DilosRender@1 (AI only — the AS article master renders through the product's
    // RenderDelimitedText@1, and the CK-shaped view of articles and orders is built by standard
    // nodes in the yamls), with the product's MakeHttpRequest@1 fetching and SftpUpload@1
    // delivering. Return path: WeClappArWrite@1 / WeClappBeWrite@1, with the product's SftpList@1,
    // SftpDownload@1 and SftpDelete@1 doing the SFTP mechanics and an Industry.Logistics/InboundFile
    // marker per processed file (standard nodes in the yamls) replacing the former in-memory file
    // state. Every pipeline is driven by a passive trigger from the product, so this adapter
    // declares no trigger node of its own.
    builder.Services.AddOctoMeshAdapter()
        .RegisterNode<WeClappResolveSupplySourcesNode>()
        .RegisterNode<DilosRenderNode>()
        .RegisterNode<WeClappArWriteNode>()
        .RegisterNode<WeClappBeWriteNode>();

}, app =>
{
    app.MapObservability();
    app.UseOctoMeshAdapter();
});
