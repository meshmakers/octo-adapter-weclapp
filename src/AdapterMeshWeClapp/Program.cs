using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
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

    // Add the adapter service to startup and shutdown the adapter
    builder.Services.AddSingleton<IAdapterService, AdapterMeshWeClappService>();

    // WeClapp serves gzip-compressed responses (live-verified: raw bodies start with 0x1F) —
    // the named client for the fetch node must decompress automatically.
    builder.Services.AddHttpClient(nameof(WeClappFetchTriggerNode))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

    // Add mesh adapter nodes and services to the container:
    // the three custom nodes of the ingestion design (WeClappFetch → WeClappToCk → DilosRender).
    builder.Services.AddOctoMeshAdapter()
        .RegisterTriggerNode<WeClappFetchTriggerNode>()
        .RegisterNode<WeClappToCkNode>()
        .RegisterNode<DilosRenderNode>();

}, app =>
{
    app.UseOctoMeshAdapter();
});
