using System.Net;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class DefaultHttpClientRegistrationTests
{
    // MakeHttpRequest@1 resolves a plain injected HttpClient, i.e. the DEFAULT named client.
    // WeClapp answers gzip, so without decompression here every paged fetch fails to parse and
    // the un-paged customer lookup stores the raw bytes as a string without any error at all.
    [Fact]
    public void AddWeClappHttpClients_ConfiguresTheDefaultClientForAutomaticDecompression()
    {
        Assert.Equal(DecompressionMethods.All,
            PrimaryHandlerOf(Options.DefaultName).AutomaticDecompression);
    }

    // Each named client is named after the node type that resolves it, so a node type and its
    // client entry have to disappear together. Pinning the exact SET rather than "the default one
    // is configured" catches both halves of that: an entry left behind for a node that no longer
    // exists, and a lost entry for a node that still sends WeClapp requests - the latter surfacing
    // otherwise only as an unreadable gzip body in the cluster.
    [Theory]
    [InlineData("")]
    [InlineData(nameof(WeClappArWriteNode))]
    [InlineData(nameof(WeClappBeWriteNode))]
    public void AddWeClappHttpClients_ConfiguresEveryWeClappClientForAutomaticDecompression(string clientName)
    {
        Assert.Equal(DecompressionMethods.All, PrimaryHandlerOf(clientName).AutomaticDecompression);
    }

    [Fact]
    public void AddWeClappHttpClients_ConfiguresNoClientBeyondTheDefaultAndTheTwoWriteNodes()
    {
        var services = new ServiceCollection();
        services.AddWeClappHttpClients();

        var configured = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Where(options => options.Action is not null)
            .Select(options => options.Name ?? "")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>([string.Empty, nameof(WeClappArWriteNode), nameof(WeClappBeWriteNode)],
                StringComparer.Ordinal),
            configured);
    }

    /// <summary>Runs the registered builder actions of one named client over a stub builder and
    /// returns the primary handler they produced - the only way to see the handler configuration
    /// without actually creating a client.</summary>
    private static HttpClientHandler PrimaryHandlerOf(string clientName)
    {
        var services = new ServiceCollection();
        services.AddWeClappHttpClients();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(clientName);

        var builder = new HttpMessageHandlerBuilderStub();
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return Assert.IsType<HttpClientHandler>(builder.PrimaryHandler);
    }

    private sealed class HttpMessageHandlerBuilderStub : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
