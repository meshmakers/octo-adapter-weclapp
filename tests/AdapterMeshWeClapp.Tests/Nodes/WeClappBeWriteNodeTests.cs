using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappBeWriteNodeTests
{
    // BE snapshot (golden 6-field layout, VER/GES states):
    //   A: 5 vs current 3      → book +2 incoming onto the default storage place
    //   B: 1 vs current 3+1    → book −3 outgoing from storage place P1
    //   C: 7 vs current 7      → in sync, nothing
    //   X: unknown in WeClapp  → skipped loudly
    //   G: GES (blocked)       → skipped loudly (semantics not agreed)
    private const string BeContent =
        "A|0|0||5|VER\r\n" +
        "B|0|0||1|VER\r\n" +
        "C|0|0||7|VER\r\n" +
        "X|0|0||3|VER\r\n" +
        "G|0|0||2|GES\r\n";

    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();

    private WeClappBeWriteNode CreateSut(FakeHttpMessageHandler handler)
    {
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));
        return new WeClappBeWriteNode(_next, A.Fake<ILogger<WeClappBeWriteNode>>(), _httpClientFactory);
    }

    private WeClappBeWriteNodeConfiguration Configure(bool dryRun = false, int pageSize = 500)
    {
        var config = new WeClappBeWriteNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            WarehouseId = "W1",
            DryRun = dryRun,
            PageSize = pageSize,
            MaxRetries = 4,
            RetryBackoffBaseSeconds = 0,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>()).Returns(config);
        A.CallTo(() => _dataContext.Get<string>("$.fileName")).Returns("BE_20240205035403463.txt");
        A.CallTo(() => _dataContext.Get<string>("$.content")).Returns(BeContent);
        return config;
    }

    private static HttpResponseMessage DefaultResponder(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("warehouse?id-eq=W1"))
        {
            return FakeHttpMessageHandler.Json(
                """{"result":[{"id":"W1","defaultStoragePlaceId":"DSP"}]}""");
        }

        if (url.Contains("/article?"))
        {
            return FakeHttpMessageHandler.Json(
                """{"result":[{"id":"A"},{"id":"B"},{"id":"C"},{"id":"G"}]}""");
        }

        if (url.Contains("warehouseStock?"))
        {
            return FakeHttpMessageHandler.Json(
                """
                {"result":[
                  {"articleId":"A","quantity":"3","storagePlaceId":"P0"},
                  {"articleId":"B","quantity":"3","storagePlaceId":"P1"},
                  {"articleId":"B","quantity":"1","storagePlaceId":"P2"},
                  {"articleId":"C","quantity":"7","storagePlaceId":"P0"}]}
                """);
        }

        return FakeHttpMessageHandler.Json("""{"result":[{}]}""");
    }

    [Fact]
    public async Task Process_BooksIncomingAndOutgoingDeltasOnly()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var posts = handler.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);

        var incoming = Assert.Single(posts, p => p.Url.Contains("bookIncomingMovement"));
        var incomingBody = JsonNode.Parse(incoming.Body!)!;
        Assert.Equal("A", incomingBody["articleId"]!.ToString());
        Assert.Equal("2", incomingBody["quantity"]!.ToString());
        Assert.Equal("DSP", incomingBody["targetStoragePlaceId"]!.ToString());
        Assert.Equal("LKV BE BE_20240205035403463.txt", incomingBody["movementNote"]!.ToString());

        var outgoing = Assert.Single(posts, p => p.Url.Contains("bookOutgoingMovement"));
        var outgoingBody = JsonNode.Parse(outgoing.Body!)!;
        Assert.Equal("B", outgoingBody["articleId"]!.ToString());
        Assert.Equal("3", outgoingBody["quantity"]!.ToString());
        Assert.Equal("P1", outgoingBody["sourceStoragePlaceId"]!.ToString());

        // Unknown article X and blocked line G must be reported, not booked.
        A.CallTo(() => _nodeContext.Error(A<string>.That.Contains("X"))).MustHaveHappenedOnceOrMore();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_DryRun_PostsNothing()
    {
        Configure(dryRun: true);
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_PagesArticleBulkReadUntilShortPage()
    {
        Configure(pageSize: 2);
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/article?"))
            {
                if (url.Contains("page=1"))
                {
                    return FakeHttpMessageHandler.Json("""{"result":[{"id":"A"},{"id":"B"}]}""");
                }

                return FakeHttpMessageHandler.Json(url.Contains("page=2")
                    ? """{"result":[{"id":"C"},{"id":"G"}]}"""
                    : """{"result":[]}""");
            }

            if (url.Contains("warehouseStock?") && !url.Contains("page=1"))
            {
                return FakeHttpMessageHandler.Json("""{"result":[]}""");
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // page 2 returned a full page (pageSize 2) → page 3 was requested and came back short.
        Assert.Equal(3, handler.Requests.Count(r => r.Url.Contains("/article?")));
        Assert.Equal(2, handler.Requests.Count(r => r.Method == "POST")); // bookings unchanged
    }

    [Fact]
    public async Task Process_WarehouseNotFoundFailsLoud()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("warehouse?id-eq=")
                ? FakeHttpMessageHandler.Json("""{"result":[]}""")
                : DefaultResponder(req));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }
}
