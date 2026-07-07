using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappArWriteNodeTests
{
    // Golden AR00006946.TXT verbatim (order 400000001247987, carrier code 9 = unmapped,
    // bare tracking number, article 400000001273682 delivered 1, over-delivery open -1).
    private const string GoldenAr =
        "K*|1|1|400000001572890||400000001247987|TEST-123|1001801714|1400137|2|10.04.2024|1|1|2,5\r\n" +
        "C*|400000001247987|9|1013408501850970172035|Karton|Standard|2,5\r\n" +
        "P*|400000001247987|1|400000001273682||||||0|1|-1\r\n" +
        "L*|400000001247987|1|400000001273682||||||1|1013408501850970172035\r\n";

    // Same shipment with a DHL carrier code (400) instead of the unmapped 9.
    private static readonly string DhlAr = GoldenAr.Replace(
        "C*|400000001247987|9|", "C*|400000001247987|400|");

    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly IHttpClientFactory _httpClientFactory = A.Fake<IHttpClientFactory>();

    private WeClappArWriteNode CreateSut(FakeHttpMessageHandler handler)
    {
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));
        return new WeClappArWriteNode(_next, A.Fake<ILogger<WeClappArWriteNode>>(), _httpClientFactory);
    }

    private WeClappArWriteNodeConfiguration Configure(string content = GoldenAr, bool dryRun = false,
        int maxRetries = 4)
    {
        var config = new WeClappArWriteNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            DryRun = dryRun,
            MaxRetries = maxRetries,
            RetryBackoffBaseSeconds = 0,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>()).Returns(config);
        A.CallTo(() => _dataContext.Get<string>("$.fileName")).Returns("AR00006946.TXT");
        A.CallTo(() => _dataContext.Get<string>("$.content")).Returns(content);
        return config;
    }

    private static HttpResponseMessage DefaultResponder(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/salesOrder/id/400000001247987"))
        {
            return FakeHttpMessageHandler.Json("""{"result":{"id":"400000001247987"}}""");
        }

        if (url.Contains("shipment?salesOrderId-eq=400000001247987"))
        {
            return FakeHttpMessageHandler.Json(
                """
                {"result":[{"id":"S1","status":"NEW","shipmentItems":[
                  {"id":"I1","articleId":"400000001273682","quantity":"1"},
                  {"id":"I2","articleId":"999","quantity":"3"}]}]}
                """);
        }

        if (url.Contains("shippingCarrier"))
        {
            return FakeHttpMessageHandler.Json(
                """{"result":[{"id":"77","name":"DHL","ecommerceShippingCarrier":"DHL"}]}""");
        }

        return FakeHttpMessageHandler.Json("""{"result":{"id":"S1"}}""");
    }

    [Fact]
    public async Task Process_ExistingNewShipment_PutsDataThenStatusShipped()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Contains("/salesOrder/id/400000001247987", handler.Requests[0].Url);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Contains("shipment?salesOrderId-eq=400000001247987", handler.Requests[1].Url);
        Assert.All(handler.Requests, r => Assert.Equal("test-key", r.AuthToken));

        // Data PUT: everything except the status.
        var (method, url, _, body) = handler.Requests[2];
        Assert.Equal("PUT", method);
        Assert.Contains("/shipment/id/S1", url);
        Assert.DoesNotContain("dryRun", url);
        var data = JsonNode.Parse(body!)!;
        Assert.Equal("1013408501850970172035", data["packageTrackingNumber"]!.ToString());
        Assert.Null(data["packageTrackingUrl"]); // bare-number carrier: no URL
        Assert.Null(data["shippingCarrierId"]); // carrier code 9 is unmapped
        Assert.Null(data["status"]); // SHIPPED is a separate, LAST write
        Assert.Equal(1712707200000, (long)data["shippingDate"]!);
        Assert.Equal("2.5", data["totalWeight"]!.ToString());
        var parcel = Assert.Single(data["parcels"]!.AsArray());
        Assert.Equal(1, (int)parcel!["positionNumber"]!);
        Assert.Equal("1013408501850970172035", parcel["trackingId"]!.ToString());
        Assert.Equal("2.5", parcel["weight"]!.ToString());

        // shipmentItems: complete list — matched item updated, unmatched echoed unchanged.
        var items = data["shipmentItems"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("I1", items[0]!["id"]!.ToString());
        Assert.Equal("1", items[0]!["quantity"]!.ToString());
        Assert.Equal("I2", items[1]!["id"]!.ToString());
        Assert.Equal("3", items[1]!["quantity"]!.ToString());

        // Status PUT: exactly {"status":"SHIPPED"}, nothing else.
        var (statusMethod, statusUrl, _, statusBody) = handler.Requests[3];
        Assert.Equal("PUT", statusMethod);
        Assert.Contains("/shipment/id/S1", statusUrl);
        var status = JsonNode.Parse(statusBody!)!.AsObject();
        Assert.Single(status);
        Assert.Equal("SHIPPED", status["status"]!.ToString());

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_NoShipment_CreatesThenUpdates()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("shipment?salesOrderId-eq="))
            {
                return FakeHttpMessageHandler.Json("""{"result":[]}""");
            }

            if (url.Contains("/createShipment"))
            {
                return FakeHttpMessageHandler.Json(
                    """
                    {"result":{"id":"S9","status":"NEW","shipmentItems":[
                      {"id":"I9","articleId":"400000001273682","quantity":"1"}]}}
                    """);
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var create = handler.Requests.Single(r => r.Url.Contains("/createShipment"));
        Assert.Equal("POST", create.Method);
        Assert.Contains("/salesOrder/id/400000001247987/createShipment", create.Url);
        Assert.Equal("{}", create.Body); // must be a real JSON object, not an empty string

        var puts = handler.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        Assert.All(puts, p => Assert.Contains("/shipment/id/S9", p.Url));
        var item = Assert.Single(JsonNode.Parse(puts[0].Body!)!["shipmentItems"]!.AsArray());
        Assert.Equal("I9", item!["id"]!.ToString());
        Assert.Equal("1", item["quantity"]!.ToString());
    }

    [Fact]
    public async Task Process_AlreadyShippedWithSameTracking_SkipsAllWrites()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("shipment?salesOrderId-eq="))
            {
                return FakeHttpMessageHandler.Json(
                    """{"result":[{"id":"S1","status":"SHIPPED","packageTrackingNumber":"1013408501850970172035"}]}""");
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.DoesNotContain(handler.Requests, r => r.Method is "PUT" or "POST");
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_UnknownOrder404_LogsDeadLetterAndConsumesFile()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext); // must not throw

        Assert.Single(handler.Requests); // only the salesOrder lookup
        A.CallTo(() => _nodeContext.Error(A<string>.That.Contains("400000001247987")))
            .MustHaveHappenedOnceOrMore();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_MappedCarrierWithEntity_WritesShippingCarrierId()
    {
        Configure(DhlAr);
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.Contains(handler.Requests, r => r.Url.Contains("shippingCarrier"));
        var dataPut = handler.Requests.First(r => r.Method == "PUT");
        Assert.Equal("77", JsonNode.Parse(dataPut.Body!)!["shippingCarrierId"]!.ToString());
    }

    [Fact]
    public async Task Process_UnmappedCarrier_DoesNotLookUpCarrierEntities()
    {
        Configure(); // golden carrier code 9
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("shippingCarrier"));
    }

    [Fact]
    public async Task Process_DryRun_PutsWithDryRunParameter()
    {
        Configure(dryRun: true);
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var puts = handler.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        Assert.All(puts, p => Assert.Contains("dryRun=true", p.Url));
    }

    [Fact]
    public async Task Process_DryRunWithoutShipment_SkipsCreateAndPuts()
    {
        Configure(dryRun: true);
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("shipment?salesOrderId-eq=")
                ? FakeHttpMessageHandler.Json("""{"result":[]}""")
                : DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // createShipment has no dry-run support — a dry run must not create anything.
        Assert.DoesNotContain(handler.Requests, r => r.Method is "PUT" or "POST");
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_TransientErrorIsRetried()
    {
        Configure(maxRetries: 4);
        var handler = new FakeHttpMessageHandler((req, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("/salesOrder/id/")));
    }

    [Fact]
    public async Task Process_ValidationErrorOnPutFailsLoud()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) => req.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"title":"quantity invalid"}"""),
            }
            : DefaultResponder(req));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("quantity invalid", ex.Message);
    }
}
