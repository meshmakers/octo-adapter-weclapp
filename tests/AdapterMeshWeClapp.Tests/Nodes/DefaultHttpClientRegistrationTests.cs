using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class DefaultHttpClientRegistrationTests
{
    // MakeHttpRequest@1 resolves a plain injected HttpClient, i.e. the DEFAULT named client.
    // WeClapp answers gzip, so without decompression here every paged fetch fails to parse and
    // the un-paged customer lookup stores the raw bytes as a string without any error at all.
    [Fact]
    public void AddWeClappHttpClients_ConfiguresTheDefaultClientForAutomaticDecompression()
    {
        var services = new ServiceCollection();
        services.AddWeClappHttpClients();

        var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(Microsoft.Extensions.Options.Options.DefaultName);

        var builder = new HttpMessageHandlerBuilderStub();
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        var handler = Assert.IsType<HttpClientHandler>(builder.PrimaryHandler);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }

    private sealed class HttpMessageHandlerBuilderStub : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
