using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappFetchStepNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();

    private WeClappFetchStepNode CreateSut(FakeHttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));
        return new WeClappFetchStepNode(_next, _httpClientFactory,
            A.Fake<ILogger<WeClappFetchStepNode>>(), timeProvider);
    }

    private WeClappFetchStepNodeConfiguration Configure(string entity, string emitMode = "PerItem",
        int pageSize = 100, string additionalQuery = "", bool enrichSupplySources = true)
    {
        var config = new WeClappFetchStepNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            Entity = entity,
            EmitMode = emitMode,
            PageSize = pageSize,
            AdditionalQuery = additionalQuery,
            EnrichSupplySources = enrichSupplySources,
            RetryBackoffBaseSeconds = 0,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<WeClappFetchStepNodeConfiguration>()).Returns(config);
        return config;
    }

    private void AssertNextCalledOnce() =>
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();

    private void AssertNextNotCalled() =>
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();

    // --- entity article, emitMode Batch: $.items + $.meta (AS collector shape) -----------

    [Fact]
    public async Task BatchArticleFetch_SeedsItemsAndMeta_AtRoot()
    {
        Configure("article", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"}]}"""));
        // 10:00 UTC = 12:00 Wien (CEST) same calendar day.
        var sut = CreateSut(handler, new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)));

        JsonArray? items = null;
        JsonObject? meta = null;
        A.CallTo(() => _dataContext.Set("$.items", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => items = (JsonArray?)call.Arguments[1]);
        A.CallTo(() => _dataContext.Set("$.meta", A<JsonObject>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => meta = (JsonObject?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(items);
        Assert.Single(items);
        Assert.Equal("1", items[0]!["id"]!.ToString());
        Assert.NotNull(meta);
        Assert.Equal("AS", meta["exportKind"]!.ToString());
        Assert.Equal("2026-07-23", meta["exportDate"]!.ToString());
        AssertNextCalledOnce();
    }

    [Fact]
    public async Task BatchArticleFetch_ZeroArticles_SeedsEmptyItems()
    {
        Configure("article", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        JsonArray? items = null;
        JsonObject? meta = null;
        A.CallTo(() => _dataContext.Set("$.items", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => items = (JsonArray?)call.Arguments[1]);
        A.CallTo(() => _dataContext.Set("$.meta", A<JsonObject>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => meta = (JsonObject?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // Unlike the trigger (which skips the execution entirely on 0 articles), the step
        // ALWAYS seeds — a missing/non-array path would abort a downstream ForEach@1 with
        // PathMustBeArray; an empty array no-ops gracefully.
        Assert.NotNull(items);
        Assert.Empty(items);
        Assert.NotNull(meta); // meta is still written even for an empty batch
        AssertNextCalledOnce();
    }

    // --- entity salesOrder: $.orders = [{ item, customer }] -------------------------------

    [Fact]
    public async Task OrderFetch_SeedsOrdersArray_ItemAndCustomerKeys()
    {
        Configure("salesOrder");
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("salesOrder")
                ? FakeHttpMessageHandler.Json("""{"result":[{"id":"o1","customerId":"7"},{"id":"o2","customerId":"7"}]}""")
                : FakeHttpMessageHandler.Json("""{"result":[{"id":"7","customerNumber":"10000"}]}""");
        });
        var sut = CreateSut(handler);

        JsonArray? orders = null;
        A.CallTo(() => _dataContext.Set("$.orders", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => orders = (JsonArray?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(orders);
        Assert.Equal(2, orders.Count);
        Assert.Equal("o1", orders[0]!["item"]!["id"]!.ToString());
        Assert.Equal("10000", orders[0]!["customer"]!["customerNumber"]!.ToString());
        Assert.Equal("o2", orders[1]!["item"]!["id"]!.ToString());
        Assert.Equal("10000", orders[1]!["customer"]!["customerNumber"]!.ToString());
        AssertNextCalledOnce();
    }

    [Fact]
    public async Task OrderFetch_ZeroOrders_SeedsEmptyOrdersArray()
    {
        Configure("salesOrder");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        JsonArray? orders = null;
        A.CallTo(() => _dataContext.Set("$.orders", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => orders = (JsonArray?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // ForEach@1 throws PathMustBeArray on a missing array — [] must always be seeded.
        Assert.NotNull(orders);
        Assert.Empty(orders);
        AssertNextCalledOnce();
    }

    // --- real DataContextImpl: proves the seeded array lands FLAT, not wrapped -----------

    // Every test above uses a FakeItEasy IDataContext, so "Set(\"$.orders\", A<JsonArray>._, …)"
    // only proves the node PASSES the right object reference to Set — it says nothing about how
    // a REAL DataContextImpl stores an array under DocumentModes.Extend +
    // TargetValueWriteModes.Overwrite, or whether a later Get<JsonArray> reads the SAME flat
    // sequence back. Precedent: AiExportGateTests/AsExportGateTests write their gate literal
    // through a real DataContextImpl "so a green test proves the literals against production
    // serialization" instead of against a fake's captured argument — this test applies the same
    // proof to the array-seeding side of WeClappFetchStep@1.
    [Fact]
    public async Task OrderFetch_RealDataContext_SeedsOrdersAsFlatArray()
    {
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("salesOrder")
                ? FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"o1","customerId":"7"},{"id":"o2","customerId":"7"}]}""")
                : FakeHttpMessageHandler.Json("""{"result":[{"id":"7","customerNumber":"10000"}]}""");
        });
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));

        var config = new WeClappFetchStepNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            Entity = "salesOrder",
            RetryBackoffBaseSeconds = 0,
        };

        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("WeClappFetchStep", 0, config, dataContext);

        var sut = new WeClappFetchStepNode(A.Fake<NodeDelegate>(), httpClientFactory,
            A.Fake<ILogger<WeClappFetchStepNode>>());

        await sut.ProcessObjectAsync(dataContext, nodeContext);

        var orders = dataContext.Get<JsonArray>("$.orders");
        Assert.NotNull(orders);
        // FLAT: exactly the 2 order elements at the top level. A single-element-wrapped result
        // (the whole array nested as ONE element) would report Count == 1 here, and the ["item"]
        // access below would throw (JsonArray has no string-key semantics).
        Assert.Equal(2, orders.Count);
        Assert.Equal("o1", orders[0]!["item"]!["id"]!.ToString());
        Assert.Equal("10000", orders[0]!["customer"]!["customerNumber"]!.ToString());
        Assert.Equal("o2", orders[1]!["item"]!["id"]!.ToString());
    }

    // --- entity article, emitMode PerItem: $.articles = [{ item }] (ck shape) -------------

    [Fact]
    public async Task PerItemArticleFetch_WrapsEachArticleInItemKey()
    {
        Configure("article", emitMode: "PerItem");
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"},{"id":"2","name":"B"}]}"""));
        var sut = CreateSut(handler);

        JsonArray? articles = null;
        A.CallTo(() => _dataContext.Set("$.articles", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => articles = (JsonArray?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(articles);
        Assert.Equal(2, articles.Count);
        Assert.NotNull(articles[0]!["item"]);
        Assert.Equal("1", articles[0]!["item"]!["id"]!.ToString());
        Assert.Equal("2", articles[1]!["item"]!["id"]!.ToString());
        AssertNextCalledOnce();
    }

    [Fact]
    public async Task PerItemArticleFetch_ZeroArticles_SeedsEmptyArray()
    {
        Configure("article", emitMode: "PerItem");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        JsonArray? articles = null;
        A.CallTo(() => _dataContext.Set("$.articles", A<JsonArray>._, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite))
            .Invokes(call => articles = (JsonArray?)call.Arguments[1]);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(articles);
        Assert.Empty(articles);
        AssertNextCalledOnce();
    }

    // --- config guards -----------------------------------------------------------------

    [Fact]
    public async Task UnknownEntity_ThrowsWeClappPipelineExecutionException()
    {
        Configure("warehouse");
        var sut = CreateSut(new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("{}")));

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        AssertNextNotCalled();
    }

    [Fact]
    public async Task UnknownEmitMode_Throws()
    {
        Configure("article", emitMode: "Bulk");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Empty(handler.Requests);
        AssertNextNotCalled();
    }

    [Fact]
    public async Task BatchModeForSalesOrder_ThrowsBeforeFetching()
    {
        // AI stays one golden file per order — a batched order document has no consumer.
        Configure("salesOrder", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Empty(handler.Requests);
        AssertNextNotCalled();
    }

    // --- HttpClient identity (gzip decompression is registered per client NAME) ----------

    [Fact]
    public async Task ProcessObjectAsync_ReusesTriggerHttpClientName_ForGzipDecompression()
    {
        // Program.cs registers AutomaticDecompression only for specific named clients
        // (Program.cs:32-44). A new name here would silently lose gzip decompression for
        // WeClapp's gzip-compressed responses and only fail on staging.
        Configure("article", emitMode: "PerItem");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _httpClientFactory.CreateClient(nameof(WeClappFetchTriggerNode)))
            .MustHaveHappenedOnceExactly();
    }
}
