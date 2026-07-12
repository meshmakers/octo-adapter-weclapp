using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappFetchTriggerNodeTests
{
    private readonly ITriggerContext _context = A.Fake<ITriggerContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();
    private readonly List<JsonNode?> _executedDocuments = new();

    private WeClappFetchTriggerNode CreateSut(FakeHttpMessageHandler handler)
    {
        A.CallTo(() => _context.NodeContext).Returns(_nodeContext);
        A.CallTo(() => _context.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call => _executedDocuments.Add((JsonNode?)call.Arguments[1]))
            .Returns(Task.FromResult<object?>(null));
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._))
            .Returns(new HttpClient(handler));
        return new WeClappFetchTriggerNode(A.Fake<ILogger<WeClappFetchTriggerNode>>(), _httpClientFactory);
    }

    private WeClappFetchTriggerNodeConfiguration Configure(string entity, int pageSize = 100,
        string additionalQuery = "", int maxRetries = 4, string emitMode = "PerItem")
    {
        var config = new WeClappFetchTriggerNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            Entity = entity,
            PageSize = pageSize,
            AdditionalQuery = additionalQuery,
            MaxRetries = maxRetries,
            RetryBackoffBaseSeconds = 0,
            EmitMode = emitMode,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>()).Returns(config);
        return config;
    }

    [Fact]
    public async Task FetchOnce_ArticleMode_PagesUntilShortPageAndExecutesPerItem()
    {
        Configure("article", pageSize: 2);
        var handler = new FakeHttpMessageHandler((req, n) => n switch
        {
            1 => FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"},{"id":"2","name":"B"}]}"""),
            _ => FakeHttpMessageHandler.Json("""{"result":[{"id":"3","name":"C"}]}"""),
        });
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, handler.Requests.Count); // page 2 was short → no page 3
        Assert.Contains("article?page=1&pageSize=2", handler.Requests[0].Url);
        Assert.Contains("article?page=2&pageSize=2", handler.Requests[1].Url);
        Assert.All(handler.Requests, r => Assert.Equal("test-key", r.AuthToken));
        Assert.Equal(3, _executedDocuments.Count);
        Assert.Equal("1", _executedDocuments[0]!["item"]!["id"]!.ToString());
        Assert.Equal("3", _executedDocuments[2]!["item"]!["id"]!.ToString());
    }

    [Fact]
    public async Task FetchOnce_ArticleMode_EnrichesSupplySourceStubsWithEntities()
    {
        // Raw articles embed only supply-source REFERENCE STUBS ({articleSupplySourceId});
        // prices live on the separate articleSupplySource entity (customer-verified
        // 2026-07-08) — the trigger resolves the stubs with ONE entity fetch per poll.
        Configure("article");
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("articleSupplySource")
                ? FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"S1","articleNumber":"000123","articlePrices":[{"price":"12.34"}]}]}""")
                : FakeHttpMessageHandler.Json(
                    """
                    {"result":[
                      {"id":"1","supplySources":[{"id":"ref1","articleSupplySourceId":"S1"}]},
                      {"id":"2","supplySources":[]}
                    ]}
                    """));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Single(handler.Requests, r => r.Url.Contains("articleSupplySource"));
        Assert.Equal(2, _executedDocuments.Count);
        var enriched = _executedDocuments[0]!["item"]!["supplySources"]!.AsArray();
        Assert.Equal("12.34", enriched[0]!["articlePrices"]![0]!["price"]!.ToString());
        Assert.Empty(_executedDocuments[1]!["item"]!["supplySources"]!.AsArray());
    }

    [Fact]
    public async Task FetchOnce_ArticleMode_NoStubsSkipsSupplySourceFetch()
    {
        Configure("article");
        var handler = new FakeHttpMessageHandler((req, _) =>
            FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"}]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("articleSupplySource"));
    }

    [Fact]
    public async Task FetchOnce_ZeroMaxRetries_StillTriesOnce()
    {
        // A misconfigured retry count of 0 must not skip the request entirely
        // (the old loop would throw "failed after 0 attempts (null)").
        Configure("article", maxRetries: 0);
        var handler = new FakeHttpMessageHandler((req, _) =>
            FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchOnce_ArticleMode_EmptyResultExecutesNothing()
    {
        Configure("article");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Single(handler.Requests);
        Assert.Empty(_executedDocuments);
    }

    [Fact]
    public async Task FetchOnce_SalesOrderMode_JoinsCustomerAndCachesLookups()
    {
        Configure("salesOrder", pageSize: 10, additionalQuery: "status-eq=ORDER_ENTRY_IN_PROGRESS");
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("salesOrder"))
            {
                return FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"o1","customerId":"7"},{"id":"o2","customerId":"7"}]}""");
            }

            return FakeHttpMessageHandler.Json("""{"result":[{"id":"7","customerNumber":"10000"}]}""");
        });
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        // salesOrder query carries the additional filter; NO additionalProperties parameter
        // (live-verified: it does not exist, orderItems are in the default response).
        var orderRequest = handler.Requests[0].Url;
        Assert.Contains("salesOrder?page=1&pageSize=10", orderRequest);
        Assert.DoesNotContain("additionalProperties", orderRequest);
        Assert.Contains("status-eq=ORDER_ENTRY_IN_PROGRESS", orderRequest);

        // one customer lookup only (cached for the second order), via verified -eq filter syntax
        var customerRequests = handler.Requests.Where(r => r.Url.Contains("customer?")).ToList();
        var customerRequest = Assert.Single(customerRequests);
        Assert.Contains("id-eq=7", customerRequest.Url);

        Assert.Equal(2, _executedDocuments.Count);
        Assert.Equal("o1", _executedDocuments[0]!["item"]!["id"]!.ToString());
        Assert.Equal("10000", _executedDocuments[0]!["customer"]!["customerNumber"]!.ToString());
        Assert.Equal("o2", _executedDocuments[1]!["item"]!["id"]!.ToString());
    }

    [Fact]
    public async Task FetchOnce_TransientErrorRetriesThenSucceeds()
    {
        Configure("article", maxRetries: 4);
        var handler = new FakeHttpMessageHandler((_, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchOnce_TransientErrorsExhaustRetriesThenThrow()
    {
        Configure("article", maxRetries: 3);
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchOnce_NonTransient401FailsWithoutRetry()
    {
        Configure("article", maxRetries: 4);
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchOnce_UnknownEntityThrows()
    {
        Configure("warehouse");
        var sut = CreateSut(new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("{}")));

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));
    }

    // --- emitMode: Batch (AS collector) ---------------------------------------------------
    // Golden precedent: Billbee delivers ONE AS file per run containing ALL articles
    // (AS20240206020204.txt, 46 lines) — per-item execution would produce N files per poll.
    // Batch mode emits a single execution shaped { "items": [ … ] } for the AS pipeline;
    // the AI/order pipeline stays per-item (one golden AI file per order).

    [Fact]
    public async Task FetchOnce_ArticleBatchMode_ExecutesOnceWithAllItems()
    {
        Configure("article", pageSize: 2, emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((req, n) => n switch
        {
            1 => FakeHttpMessageHandler.Json("""{"result":[{"id":"1","name":"A"},{"id":"2","name":"B"}]}"""),
            _ => FakeHttpMessageHandler.Json("""{"result":[{"id":"3","name":"C"}]}"""),
        });
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        var document = Assert.Single(_executedDocuments);
        var items = document!["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal("1", items[0]!["id"]!.ToString());
        Assert.Equal("3", items[2]!["id"]!.ToString());
        Assert.Null(document["item"]); // batch shape only — no stray per-item key
    }

    [Fact]
    public async Task FetchOnce_ArticleBatchMode_StillEnrichesSupplySources()
    {
        Configure("article", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("articleSupplySource")
                ? FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"S1","articleNumber":"000123","articlePrices":[{"price":"12.34"}]}]}""")
                : FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"1","supplySources":[{"id":"ref1","articleSupplySourceId":"S1"}]}]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        var document = Assert.Single(_executedDocuments);
        var enriched = document!["items"]![0]!["supplySources"]!.AsArray();
        Assert.Equal("12.34", enriched[0]!["articlePrices"]![0]!["price"]!.ToString());
    }

    [Fact]
    public async Task FetchOnce_ArticleBatchMode_EmptyResultExecutesNothing()
    {
        // No articles → no execution at all (an empty AS upload would be a lie of a snapshot).
        Configure("article", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.FetchOnceAsync(_context);

        Assert.Empty(_executedDocuments);
    }

    [Fact]
    public async Task FetchOnce_BatchModeForSalesOrder_ThrowsBeforeFetching()
    {
        // AI is one golden file per order — a batched order execution has no consumer and
        // would silently break the per-order file contract. Config error, checked upfront.
        Configure("salesOrder", emitMode: "Batch");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchOnce_UnknownEmitModeThrows()
    {
        Configure("article", emitMode: "Bulk");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(() => sut.FetchOnceAsync(_context));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StartAndStop_TerminateCleanly()
    {
        Configure("article");
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = CreateSut(handler);

        await sut.StartAsync(_context);
        await Task.Delay(50); // let the first poll run
        var stop = sut.StopAsync(_context);
        var finished = await Task.WhenAny(stop, Task.Delay(5000));

        Assert.Same(stop, finished); // stop must not hang
        Assert.NotEmpty(handler.Requests);
    }
}
