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
        string additionalQuery = "", int maxRetries = 4)
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
