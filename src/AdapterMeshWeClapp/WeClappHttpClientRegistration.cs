using System.Net;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp;

/// <summary>
/// WeClapp serves gzip-compressed responses, so every client this adapter sends WeClapp requests
/// through must decompress automatically. That includes the DEFAULT client: the standard
/// <c>MakeHttpRequest@1</c> node resolves a plain injected <see cref="HttpClient"/>, which the
/// product registers as the default named client without any handler configuration.
/// </summary>
public static class WeClappHttpClientRegistration
{
    /// <summary>Registers the WeClapp HTTP clients (the default one plus one per node that resolves
    /// a client of its own) with automatic decompression.</summary>
    public static IServiceCollection AddWeClappHttpClients(this IServiceCollection services)
    {
        foreach (var clientName in new[]
                 {
                     string.Empty, nameof(WeClappArWriteNode), nameof(WeClappBeWriteNode),
                 })
        {
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                });
        }

        return services;
    }
}
