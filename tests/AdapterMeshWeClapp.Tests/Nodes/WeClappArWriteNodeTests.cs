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
    // Golden AR00006946.TXT verbatim (order 400000001247987, carrier code 9 = ÖPAG legacy
    // fallback → AUSTRIAN_POST, no matching entity in the mock carrier list,
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

        // Full shipment for the GET → mutate → full-PUT round trip (v1 validates the PUT
        // body as a complete shipment — live-proven: "recipientPartyId is required").
        // id GETs return the BARE object without a result wrapper (live-proven 2026-07-07).
        if (url.Contains("/shipment/id/") && req.Method == HttpMethod.Get)
        {
            var id = url.Contains("/shipment/id/S9") ? "S9" : "S1";
            var itemId = id == "S9" ? "I9" : "I1";
            return FakeHttpMessageHandler.Json(
                """
                {"id":"__ID__","version":"3","status":"NEW","recipientPartyId":"4711",
                  "salesOrderId":"400000001247987","shipmentItems":[
                  {"id":"__ITEM__","articleId":"400000001273682","quantity":"9","note":"keep"},
                  {"id":"I2","articleId":"999","quantity":"3"}]}
                """.Replace("__ID__", id).Replace("__ITEM__", itemId));
        }

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
    public async Task Process_ExistingNewShipment_FullPutsDataThenStatusShipped()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // GET order, GET shipments (idempotency), GET carrier list (C* field 3 token must be
        // checked against the live entity ids), GET full shipment, data PUT, then the
        // status LADDER (trial-proven: NEW→SHIPPED directly is rejected, the transition
        // must step through DELIVERY_NOTE_PRINTED): fresh GET + PUT per rung —
        // v1 requires the complete shipment in every PUT.
        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Contains("/salesOrder/id/400000001247987", handler.Requests[0].Url);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Contains("shipment?salesOrderId-eq=400000001247987", handler.Requests[1].Url);
        Assert.Equal(("GET", true), (handler.Requests[2].Method, handler.Requests[2].Url.Contains("shippingCarrier")));
        Assert.Equal(("GET", true), (handler.Requests[3].Method, handler.Requests[3].Url.Contains("/shipment/id/S1")));
        Assert.Equal(("GET", true), (handler.Requests[5].Method, handler.Requests[5].Url.Contains("/shipment/id/S1")));
        Assert.Equal(("GET", true), (handler.Requests[7].Method, handler.Requests[7].Url.Contains("/shipment/id/S1")));
        Assert.All(handler.Requests, r => Assert.Equal("test-key", r.AuthToken));

        // Data PUT: the full fetched shipment with the AR fields merged in — status untouched.
        var (method, url, _, body) = handler.Requests[4];
        Assert.Equal("PUT", method);
        Assert.Contains("/shipment/id/S1", url);
        Assert.DoesNotContain("dryRun", url);
        var data = JsonNode.Parse(body!)!;
        Assert.Equal("4711", data["recipientPartyId"]!.ToString()); // fetched field survives
        Assert.Equal("3", data["version"]!.ToString()); // optimistic-locking echo
        Assert.Equal("NEW", data["status"]!.ToString()); // SHIPPED is a separate, LAST write
        Assert.Equal("1013408501850970172035", data["packageTrackingNumber"]!.ToString());
        Assert.Null(data["packageTrackingUrl"]); // bare-number carrier: no URL
        // Code 9 → AUSTRIAN_POST fallback, but the mock list has no such entity (only DHL):
        // tracking is written without a carrier reference.
        Assert.Null(data["shippingCarrierId"]);
        Assert.Equal(1712707200000, (long)data["shippingDate"]!);
        Assert.Equal("2.5", data["totalWeight"]!.ToString());
        // The fetched shipment has no parcels: adding parcels is forbidden while the flat
        // package* fields are in use (live 409) — tracking stays on the shipment level.
        Assert.Null(data["parcels"]);

        // shipmentItems: full objects patched in place — matched quantity updated, every
        // other field (and unmatched items) preserved.
        var items = data["shipmentItems"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("I1", items[0]!["id"]!.ToString());
        Assert.Equal("1", items[0]!["quantity"]!.ToString());
        Assert.Equal("400000001273682", items[0]!["articleId"]!.ToString());
        Assert.Equal("keep", items[0]!["note"]!.ToString());
        Assert.Equal("I2", items[1]!["id"]!.ToString());
        Assert.Equal("3", items[1]!["quantity"]!.ToString());

        // Status ladder PUTs: full shipment each, DELIVERY_NOTE_PRINTED first, SHIPPED last.
        var (rung1Method, rung1Url, _, rung1Body) = handler.Requests[6];
        Assert.Equal("PUT", rung1Method);
        Assert.Contains("/shipment/id/S1", rung1Url);
        var rung1 = JsonNode.Parse(rung1Body!)!;
        Assert.Equal("DELIVERY_NOTE_PRINTED", rung1["status"]!.ToString());
        Assert.Equal("4711", rung1["recipientPartyId"]!.ToString());

        var (rung2Method, rung2Url, _, rung2Body) = handler.Requests[8];
        Assert.Equal("PUT", rung2Method);
        Assert.Contains("/shipment/id/S1", rung2Url);
        var rung2 = JsonNode.Parse(rung2Body!)!;
        Assert.Equal("SHIPPED", rung2["status"]!.ToString());
        Assert.Equal("4711", rung2["recipientPartyId"]!.ToString());

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Process_ShipmentAlreadyDeliveryNotePrinted_OnlyStepsToShipped()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/shipment/id/S1") && req.Method == HttpMethod.Get)
            {
                return FakeHttpMessageHandler.Json(
                    """
                    {"id":"S1","version":"5","status":"DELIVERY_NOTE_PRINTED","recipientPartyId":"4711",
                      "shipmentItems":[]}
                    """);
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var statusPuts = handler.Requests.Where(r => r.Method == "PUT")
            .Select(r => JsonNode.Parse(r.Body!)!["status"]!.ToString())
            .ToList();
        // Data PUT echoes the current status; the ladder adds only the missing rung.
        Assert.Equal(new[] { "DELIVERY_NOTE_PRINTED", "SHIPPED" }, statusPuts);
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
        Assert.Equal(3, puts.Count); // data + status ladder (DELIVERY_NOTE_PRINTED, SHIPPED)
        Assert.All(puts, p => Assert.Contains("/shipment/id/S9", p.Url));
        var data = JsonNode.Parse(puts[0].Body!)!;
        Assert.Equal("4711", data["recipientPartyId"]!.ToString()); // full-shipment round trip
        var item = data["shipmentItems"]!.AsArray()
            .Single(i => i!["id"]!.ToString() == "I9");
        Assert.Equal("1", item!["quantity"]!.ToString()); // delivered quantity patched in place
        Assert.Equal("DELIVERY_NOTE_PRINTED", JsonNode.Parse(puts[1].Body!)!["status"]!.ToString());
        Assert.Equal("SHIPPED", JsonNode.Parse(puts[2].Body!)!["status"]!.ToString());
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
    public async Task Process_ShipmentWithMatchingParcels_PatchesThemInPlace()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/shipment/id/S1") && req.Method == HttpMethod.Get)
            {
                return FakeHttpMessageHandler.Json(
                    """
                    {"id":"S1","version":"3","status":"NEW","recipientPartyId":"4711",
                      "shipmentItems":[],
                      "parcels":[{"id":"PC1","positionNumber":1,"reference":"keep-me","weight":"9.9"}]}
                    """);
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var dataPut = handler.Requests.First(r => r.Method == "PUT");
        var parcel = Assert.Single(JsonNode.Parse(dataPut.Body!)!["parcels"]!.AsArray());
        Assert.Equal("PC1", parcel!["id"]!.ToString()); // same parcel object — no add/remove
        Assert.Equal("keep-me", parcel["reference"]!.ToString()); // untouched fields survive
        Assert.Equal("1013408501850970172035", parcel["trackingId"]!.ToString());
        Assert.Equal("2.5", parcel["weight"]!.ToString());
    }

    [Fact]
    public async Task Process_ParcelCountMismatch_LeavesParcelsUntouched()
    {
        Configure();
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/shipment/id/S1") && req.Method == HttpMethod.Get)
            {
                return FakeHttpMessageHandler.Json(
                    """
                    {"id":"S1","version":"3","status":"NEW","recipientPartyId":"4711",
                      "shipmentItems":[],
                      "parcels":[{"id":"PC1","positionNumber":1},{"id":"PC2","positionNumber":2}]}
                    """);
            }

            return DefaultResponder(req);
        });
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext); // AR has 1 parcel, WeClapp 2

        var dataPut = handler.Requests.First(r => r.Method == "PUT");
        var parcels = JsonNode.Parse(dataPut.Body!)!["parcels"]!.AsArray();
        Assert.Equal(2, parcels.Count);
        Assert.All(parcels, p => Assert.Null(p!["trackingId"])); // nothing guessed by index
        A.CallTo(() => _nodeContext.Error(A<string>.That.Contains("parcel count")))
            .MustHaveHappenedOnceOrMore();
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
    public async Task Process_CarrierTokenMatchingEntityId_WritesThatShippingCarrierId()
    {
        // Jürgen 2026-07-08: LKV returns the carrier id as configured in the shop system —
        // for WeClapp that is the shippingCarrier entity id itself. "77" is no DILOS code,
        // but it IS the id of the mock carrier entity → direct reference, no mapping table.
        Configure(GoldenAr.Replace("C*|400000001247987|9|", "C*|400000001247987|77|"));
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var dataPut = handler.Requests.First(r => r.Method == "PUT");
        Assert.Equal("77", JsonNode.Parse(dataPut.Body!)!["shippingCarrierId"]!.ToString());
    }

    [Fact]
    public async Task Process_UnresolvableCarrierToken_LooksUpOnceButWritesNoCarrierId()
    {
        // "123" is neither a legacy DILOS/Billbee code nor an existing entity id: the node
        // must consult the carrier list (it cannot know otherwise), then write tracking
        // without a carrier reference.
        Configure(GoldenAr.Replace("C*|400000001247987|9|", "C*|400000001247987|123|"));
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.Contains(handler.Requests, r => r.Url.Contains("shippingCarrier"));
        var dataPut = handler.Requests.First(r => r.Method == "PUT");
        Assert.Null(JsonNode.Parse(dataPut.Body!)!["shippingCarrierId"]);
    }

    [Fact]
    public async Task Process_DryRun_PutsWithDryRunParameter()
    {
        Configure(dryRun: true);
        var handler = new FakeHttpMessageHandler((req, _) => DefaultResponder(req));
        var sut = CreateSut(handler);

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var puts = handler.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(3, puts.Count); // data + both status-ladder rungs
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
