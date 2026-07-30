using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
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
    //   N: not STORABLE        → skipped loudly (bookings on it are rejected — trial-proven 400)
    private const string BeContent =
        "A|0|0||5|VER\r\n" +
        "B|0|0||1|VER\r\n" +
        "C|0|0||7|VER\r\n" +
        "X|0|0||3|VER\r\n" +
        "G|0|0||2|GES\r\n" +
        "N|0|0||4|VER\r\n";

    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();

    private WeClappBeWriteNode CreateSut(FakeHttpMessageHandler handler)
    {
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));
        return new WeClappBeWriteNode(_next, A.Fake<ILogger<WeClappBeWriteNode>>(), _httpClientFactory, _etlContext);
    }

    [Fact]
    public async Task Process_ApiConfiguration_UsesResolvedBaseUrlAndKey()
    {
        var config = Configure();
        config.ApiConfiguration = "WeClappApi";
        config.BaseUrl = null;
        config.ApiKey = null;
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.StartsWith("https://cfg.weclapp.com/webapp/api/v1/", r.Url));
        Assert.All(handler.Requests, r => Assert.Equal("cfg-key", r.AuthToken));
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
                """
                {"result":[{"id":"A","articleType":"STORABLE"},{"id":"B","articleType":"STORABLE"},
                  {"id":"C","articleType":"STORABLE"},{"id":"G","articleType":"STORABLE"},
                  {"id":"N","articleType":"SALES"}]}
                """);
        }

        if (url.Contains("warehouseStock?"))
        {
            // Live rows always carry warehouseId (the node re-checks it locally).
            return FakeHttpMessageHandler.Json(
                """
                {"result":[
                  {"articleId":"A","quantity":"3","storagePlaceId":"P0","warehouseId":"W1"},
                  {"articleId":"B","quantity":"3","storagePlaceId":"P1","warehouseId":"W1"},
                  {"articleId":"B","quantity":"1","storagePlaceId":"P2","warehouseId":"W1"},
                  {"articleId":"C","quantity":"7","storagePlaceId":"P0","warehouseId":"W1"}]}
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

        // Unknown article X, blocked line G and non-storable N must be reported, not booked
        // (the warning text is a template arg, so it is matched in the args array).
        A.CallTo(() => _nodeContext.Error(A<string>._,
                A<object[]>.That.Matches(a => a.Any(o => o.ToString()!.Contains("X")))))
            .MustHaveHappenedOnceOrMore();
        A.CallTo(() => _nodeContext.Error(A<string>.That.Contains("not storable"), A<object[]>._))
            .MustHaveHappenedOnceOrMore();
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
                    return FakeHttpMessageHandler.Json(
                        """{"result":[{"id":"A","articleType":"STORABLE"},{"id":"B","articleType":"STORABLE"}]}""");
                }

                return FakeHttpMessageHandler.Json(url.Contains("page=2")
                    ? """{"result":[{"id":"C","articleType":"STORABLE"},{"id":"G","articleType":"STORABLE"}]}"""
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
    public async Task Process_RejectedMovementDoesNotBlockRemainingMovements()
    {
        // A permanently rejected booking line must not wedge the whole file: the movements
        // after it are still posted (already-booked ones are delta 0 on the retry run), and
        // the file-level throw keeps the file on the server for retry.
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("bookIncomingMovement")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"validation failed"}""")
                }
                : DefaultResponder(req));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Single(handler.Requests, r => r.Method == "POST" && r.Url.Contains("bookOutgoingMovement"));
        Assert.Contains("1 of 2", ex.Message);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Process_ForeignWarehouseRowsAreExcludedFromDeltas()
    {
        // Belt and braces for the server-side warehouseId-eq filter: a foreign-warehouse row
        // in the response must never change the configured warehouse's delta.
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("warehouseStock?")
                ? FakeHttpMessageHandler.Json(
                    """
                    {"result":[
                      {"articleId":"A","quantity":"3","storagePlaceId":"P0","warehouseId":"W1"},
                      {"articleId":"A","quantity":"100","storagePlaceId":"F1","warehouseId":"W-OTHER"},
                      {"articleId":"B","quantity":"3","storagePlaceId":"P1","warehouseId":"W1"},
                      {"articleId":"B","quantity":"1","storagePlaceId":"P2","warehouseId":"W1"},
                      {"articleId":"C","quantity":"7","storagePlaceId":"P0","warehouseId":"W1"}]}
                    """)
                : DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var posts = handler.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);
        var incoming = Assert.Single(posts, p => p.Url.Contains("bookIncomingMovement"));
        // 5 − 3 from W1 only; with the foreign row counted it would be outgoing 98.
        Assert.Equal("2", JsonNode.Parse(incoming.Body!)!["quantity"]!.ToString());
    }

    [Fact]
    public async Task Process_StockRowsWithoutWarehouseIdField_FailLoud()
    {
        // Silently dropping unattributable rows would read existing stock as 0 and
        // re-book the full BE quantities on top — the node must fail loud instead.
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("warehouseStock?")
                ? FakeHttpMessageHandler.Json(
                    """{"result":[{"articleId":"A","quantity":"3","storagePlaceId":"P0"}]}""")
                : DefaultResponder(req));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("no warehouseId field", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Process_PlatformDryRunSuppressesBookings()
    {
        // The SDK's per-execution dry-run mode must suppress real bookings even when the
        // pipeline configuration itself is not in dry-run.
        Configure(dryRun: false);
        A.CallTo(() => _nodeContext.PipelineExecutionMode)
            .Returns(new DefaultPipelineExecutionMode { IsDryRun = true });
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_TransientFailureAbortsTheFileImmediately()
    {
        // Persistent rejects are collected per movement, but a transient failure (5xx after
        // retries) aborts the file — during an outage the remaining movements must not each
        // burn their own retry ladder against the down API.
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"maintenance"}""")
                }
                : DefaultResponder(req));
        var sut = CreateSut(handler);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Equal(4, handler.Requests.Count(r => r.Url.Contains("bookIncomingMovement")));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("bookOutgoingMovement"));
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
