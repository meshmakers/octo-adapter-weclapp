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

    // A stub that resolves to nothing used to be dropped in silence, and the article then rendered
    // EK-Preis 0 - which is a LEGITIMATE value for an article without a purchase price, so neither
    // the delivered file nor anything downstream could tell the two apart. The AS delivery burns
    // the per-day marker on its way out, so that file would stand at LKV for the whole Vienna day.
    // A throw costs the next tick and no data. Live census of the customer account (2026-08-28):
    // 48 articles, 16 entities, 15 stubs, zero of them dangling - live data does not reach here.
    [Fact]
    public async Task StubWithoutAMatch_FailsNamingTheArticleAndTheReference()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{"articleSupplySourceId":"missing"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        // Both ends of the broken join, so the message alone identifies the master-data record:
        Assert.Contains("article ", ex.Message);
        Assert.Contains("missing", ex.Message);
        Assert.False(data.Exists("$.items"));
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    // Same rule for a stub that names nothing at all: it cannot resolve either, and the article
    // would carry EK-Preis 0 without a trace.
    [Fact]
    public async Task StubWithoutAReference_FailsNamingTheStubIndex()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("<none>", ex.Message);
        Assert.Contains("stub 0", ex.Message);
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

    // An id is the only thing an article stub can point at, so an entity without one is
    // unreachable: every stub aimed at it resolves to nothing and the price is lost silently.
    [Fact]
    public async Task SupplySourceWithoutAnId_FailsNamingTheEntityIndex()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"articlePrices":[{"price":"1"}]},{"id":"9001","articlePrices":[{"price":"987"}]}]}
            """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        // The index is what makes an unreachable entity findable inside a fetched page.
        Assert.Contains("entity 0", ex.Message);
        Assert.Contains("$.supplySources", ex.Message);
        Assert.False(data.Exists("$.items"));
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

    // ---- the two WeClapp rules a column model cannot express (spec: adapter-half step 1) ----

    // Column 20 of the AS layout is the only column that needs a RULE rather than a path read,
    // and the rule is entirely WeClapp: the first parseable supplySources[].articlePrices[].price
    // of the resolved shape, formatted with the invariant 0.#### the golden files show. A column
    // renderer can only read a path, so the finished scalar has to exist before it runs.
    [Fact]
    public async Task ProjectsThePurchasePriceAsAFinishedDilosScalar()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"4262","articleNumber":"000123","articleType":"STORABLE",
              "supplySources":[{"articleSupplySourceId":"9001"}]}],
             "supplySources":[{"id":"9001","articlePrices":[{"price":"1.6200"}]}]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        // 1.6200 -> 1.62: trailing zeros are trimmed, the separator is a dot, and the value is a
        // STRING so no downstream re-formatting can reintroduce a culture.
        Assert.Equal("1.62", data.Get<string>("$.items[0].ekPreis"));
    }

    // The zero rule is load-bearing and visible in the delivered files: column 20 is filled on
    // every line and 0 occurs among its values, whereas a plain path read of a missing price
    // would render an empty field.
    [Fact]
    public async Task ArticleWithoutASupplySourcePrice_ProjectsZeroRatherThanNothing()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"4262","articleNumber":"000123","articleType":"STORABLE",
              "supplySources":[]}],
             "supplySources":[]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        Assert.Equal("0", data.Get<string>("$.items[0].ekPreis"));
    }

    // System articles (loading equipment such as pallets) never belong in the article master
    // delivery. The render used to drop them; a column renderer emits one line per element and
    // cannot, so the step that already touches the articles does it.
    [Fact]
    public async Task DropsSystemArticles_AndKeepsTheRest()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[
               {"id":"4250","articleNumber":"Default loading equipment","articleType":"LOADING_EQUIPMENT",
                "supplySources":[]},
               {"id":"4262","articleNumber":"000123","articleType":"STORABLE","supplySources":[]}],
             "supplySources":[]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        var items = data.Get<JsonArray>("$.items");
        Assert.NotNull(items);
        Assert.Equal("4262", Assert.Single(items)!["id"]!.ToString());
    }

    // A batch of nothing but loading equipment resolves to an EMPTY array, not to a missing path:
    // the delivery is gated on the rendered content being non-empty, and a missing path would
    // read as null there, which is not equal to the empty string and would let the gate open.
    [Fact]
    public async Task BatchOfOnlySystemArticles_WritesAnEmptyArray()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"4250","articleType":"LOADING_EQUIPMENT","supplySources":[]}],
             "supplySources":[]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        var items = data.Get<JsonArray>("$.items");
        Assert.NotNull(items);
        Assert.Empty(items);
        A.CallTo(() => _next(data, node)).MustHaveHappenedOnceExactly();
    }

    // WeClapp never returns a non-object element, but a mis-aimed path can: an array of ids is
    // still an array, so the array guard above passes it. Measured before this guard existed: a
    // JSON null travelled through to the renderer as a phantom record, and any other non-object
    // failed deep inside System.Text.Json with "The node must be of type 'JsonObject'" - loud, but
    // naming neither this node nor which element.
    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public async Task NonObjectArticleElement_FailsNamingTheNodeAndTheIndex(string element)
    {
        var config = Configure();
        var (data, node) = Context(
            $$"""
              {"rawArticles":[{"id":"1","articleType":"STORABLE","supplySources":[]},{{element}}],
               "supplySources":[]}
              """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("WeClappResolveSupplySources", ex.Message);
        Assert.Contains("1", ex.Message);            // the index of the offending element
        Assert.Contains("$.rawArticles", ex.Message);
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    // An explicit "supplySources": null is what an ABSENT property means - no supply sources, so
    // no price, so EK-Preis 0. It still has to be normalised rather than passed on:
    // System.Text.Json does not enforce nullable annotations, so an explicit null lands on the
    // model OVER its initializer, and the price walk then failed as "Value cannot be null.
    // (Parameter 'source')" - measured against this exact document before the normalisation existed.
    [Fact]
    public async Task ExplicitNullSupplySources_ReadsAsNoneAndProjectsZero()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","articleType":"STORABLE","supplySources":null}],
             "supplySources":[]}
            """, config);

        await new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node);

        Assert.Equal("0", data.Get<string>("$.items[0].ekPreis"));
        A.CallTo(() => _next(data, node)).MustHaveHappenedOnceExactly();
    }

    // A present-but-non-array supplySources reached the raw AsArray() cast and threw "The node
    // must be of type JsonArray" - naming neither this node, the property nor the element, forty
    // lines below the guard that was built to do exactly that for the article itself.
    [Theory]
    [InlineData("""{"articleSupplySourceId":"9001"}""")]
    [InlineData("\"9001\"")]
    public async Task NonArraySupplySources_FailsNamingTheNodeAndTheElement(string value)
    {
        var config = Configure();
        var (data, node) = Context(
            $$"""
              {"rawArticles":[{"id":"1","articleType":"STORABLE","supplySources":{{value}}}],
               "supplySources":[]}
              """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("WeClappResolveSupplySources", ex.Message);
        Assert.Contains("supplySources", ex.Message);
        Assert.Contains("$.rawArticles", ex.Message);
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    // WeClapp money and identifier fields are strings, but a shape change (or a path aimed at a
    // differently-shaped array of objects) hands the model a number. That failed as a bare
    // JsonException naming the JSON path only - WHICH article it came from was not in the message.
    [Fact]
    public async Task ArticleThatDoesNotMatchTheModel_FailsNamingTheNodeAndTheElement()
    {
        var config = Configure();
        var (data, node) = Context("""
            {"rawArticles":[{"id":"1","articleType":"STORABLE","supplySources":[]},
                            {"id":"2","articleType":"STORABLE","ean":9120103151353,"supplySources":[]}],
             "supplySources":[]}
            """, config);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => new WeClappResolveSupplySourcesNode(_next).ProcessObjectAsync(data, node));

        Assert.Contains("WeClappResolveSupplySources", ex.Message);
        Assert.Contains("element 1", ex.Message);          // WHICH article, not just which path
        Assert.Contains("$.rawArticles", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);   // the original survives for the log
        A.CallTo(() => _next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }
}
