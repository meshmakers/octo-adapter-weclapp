using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappResolveSupplySourcesNodeTests
{
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();

    // A REAL DataContextImpl throughout: this node exists to produce a document shape, and a
    // faked IDataContext would only prove which object reference was handed to Set().
    private static (DataContextImpl Data, INodeContext Node) Context(
        string documentJson, WeClappResolveSupplySourcesNodeConfiguration config)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(documentJson));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        return (dataContext, rootContext.RegisterChildNode("WeClappResolveSupplySources", 0, config, dataContext));
    }

    private static WeClappResolveSupplySourcesNodeConfiguration Configure() => new()
    {
        Path = "$.rawArticles",
        SupplySourcesPath = "$.supplySources",
        TargetPath = "$.items",
    };

    [Fact]
    public async Task ResolvesEachStub_IntoTheFullSupplySourceEntity()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"4262","articleNumber":"000123",
              "supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);
        var sut = new WeClappResolveSupplySourcesNode(_next);

        await sut.ProcessObjectAsync(data, node);

        var items = data.Get<JsonArray>("$.items");
        Assert.NotNull(items);
        var sources = items[0]!["supplySources"]!.AsArray();
        Assert.Single(sources);
        Assert.Equal("987", sources[0]!["articlePrices"]![0]!["price"]!.ToString());
        // The stub key is gone: what stands there now is the entity, not the reference.
        Assert.Null(sources[0]!["articleSupplySourceId"]);
        A.CallTo(() => _next(data, node)).MustHaveHappenedOnceExactly();
    }

    // The assertion that carries the AS delivery: DilosRender reads the EK-Preis through
    // WeClappArticle.PurchasePrice, which walks supplySources[].articlePrices[].price. If the
    // enrichment produces any other shape, the column silently becomes 0 for every article.
    [Fact]
    public async Task EnrichedArticle_ParsesWithItsPurchasePrice()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"4262","articleNumber":"000123","name":"Test",
              "supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);
        var sut = new WeClappResolveSupplySourcesNode(_next);

        await sut.ProcessObjectAsync(data, node);

        var articles = data.GetArray<WeClappArticle>("$.items");
        Assert.NotNull(articles);
        Assert.Equal(987m, Assert.Single(articles)!.PurchasePrice);
    }

    [Fact]
    public async Task StubWithoutAMatch_IsDropped()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{"articleSupplySourceId":"missing"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        var items = data.Get<JsonArray>("$.items");
        Assert.Empty(items![0]!["supplySources"]!.AsArray());
    }

    [Fact]
    public async Task ArticleWithoutSupplySources_PassesThroughUntouched()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","articleNumber":"000123"}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        var items = data.Get<JsonArray>("$.items");
        Assert.Single(items!);
        Assert.Equal("000123", items![0]!["articleNumber"]!.ToString());
        Assert.Null(items[0]!["supplySources"]);
    }

    [Fact]
    public async Task EmptyArticleArray_WritesAnEmptyArray_AndContinues()
    {
        // The AS render treats an empty batch as "nothing to deliver" and stops the branch;
        // a missing target path would instead fail the run before it gets the chance.
        var config = Configure();
        var (data, node) = Context("""{"rawArticles":[],"supplySources":[]}""", config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        Assert.Empty(data.Get<JsonArray>("$.items")!);
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SupplySourceWithoutAnId_IsIgnored()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"articlePrices":[{"price":"1"}]},{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        var sources = data.Get<JsonArray>("$.items")![0]!["supplySources"]!.AsArray();
        Assert.Equal("987", Assert.Single(sources)!["articlePrices"]![0]!["price"]!.ToString());
    }

    [Fact]
    public async Task DuplicateSupplySourceId_FailsNamingTheId()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"1"}]},
                              {"id":"9001","articlePrices":[{"price":"2"}]}]}
            """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        // Both halves of the message are pinned: the ambiguous id, and the path it came from -
        // without the path an operator cannot tell which of two fetches produced the collision.
        Assert.Contains("9001", ex.Message);
        Assert.Contains("$.supplySources", ex.Message);
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSupplySourcesPath_FailsBeforeWritingAnything(string? supplySourcesPath)
    {
        // The pipeline deserializer is YamlDotNet: "supplySourcesPath:" with no value assigns
        // null over the property initializer, so null is a real state a definition can produce.
        var config = new WeClappResolveSupplySourcesNodeConfiguration
        {
            Path = "$.rawArticles",
            SupplySourcesPath = supplySourcesPath!,
            TargetPath = "$.items",
        };
        var (data, node) = Context("""{"rawArticles":[],"supplySources":[]}""", config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("SupplySourcesPath", ex.Message);
        Assert.False(data.Exists("$.items"));
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BlankTargetPath_FailsBeforeWritingAnything(string? targetPath)
    {
        var config = new WeClappResolveSupplySourcesNodeConfiguration
        {
            Path = "$.rawArticles",
            SupplySourcesPath = "$.supplySources",
            TargetPath = targetPath!,
        };
        var (data, node) = Context("""{"rawArticles":[],"supplySources":[]}""", config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("TargetPath", ex.Message);
    }

    [Fact]
    public async Task PathThatIsNotAnArray_FailsNamingThePath()
    {
        var config = Configure();
        var (data, node) = Context("""{"rawArticles":{"id":"1"},"supplySources":[]}""", config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("$.rawArticles", ex.Message);
    }

    [Fact]
    public async Task MissingSupplySourceArray_FailsInsteadOfSilentlySkippingEnrichment()
    {
        // A typo in supplySourcesPath must not read as "no supply sources exist": that would
        // deliver an AS file with every EK-Preis at 0 and still set the daily marker.
        var config = Configure();
        var (data, node) = Context("""{"rawArticles":[{"id":"1"}]}""", config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("$.supplySources", ex.Message);
    }
}
