using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// End-to-end chain over the three custom nodes with a REAL pipeline data context
/// (DataContextImpl, as the platform's own full-chain tests use): WeClapp JSON →
/// WeClappFetch (fake HTTP) → per-order document → WeClappToCk → DilosRender → AI lines.
/// The platform built-ins (GetOrCreate/ApplyChanges) need a repository and are exercised
/// in the tenant spike instead.
/// </summary>
public class PipelineChainIntegrationTests
{
    [Fact]
    public async Task WeClappOrder_FlowsThroughFetchToCkAndDilosRenderToAiLines()
    {
        // --- Phase 1: real fetch node against scripted HTTP ---
        var triggerContext = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => triggerContext.NodeContext).Returns(nodeContext);
        var fetchConfig = new WeClappFetchTriggerNodeConfiguration
        {
            BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
            ApiKey = "test-key",
            Entity = "salesOrder",
            RetryBackoffBaseSeconds = 0,
        };
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>())
            .Returns(fetchConfig);

        var handler = new FakeHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("salesOrder")
                ? FakeHttpMessageHandler.Json("""
                    {"result":[{
                      "id":"5910986621265","orderNumber":"74299","customerNumber":"7067387625809",
                      "customerId":"7","orderDate":1707177600000,
                      "deliveryAddress":{"company":"TJ Lucas","countryCode":"DE","zipcode":"51503",
                                         "street1":"Im Wielputzfeld 15a","city":"Rösrath"},
                      "orderItems":[{"positionNumber":1,"articleId":"43222003744925",
                                     "quantity":"1","netAmount":"29.99","title":"Ersatzglas VOLT"}],
                      "shippingCostItems":[{"netAmount":"4.50","title":"DHL Standard (DE)"}]
                    }]}
                    """)
                : FakeHttpMessageHandler.Json("""
                    {"result":[{"id":"7","customerNumber":"7067387625809","company":"TJ Lucas GmbH",
                                "email":"tj@example.com",
                                "addresses":[{"street1":"Im Wielputzfeld 15a","zipcode":"51503",
                                              "city":"Rösrath","countryCode":"DE"}]}]}
                    """));
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(handler));

        JsonNode? document = null;
        A.CallTo(() => triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call => document = (JsonNode?)call.Arguments[1])
            .Returns(Task.FromResult<object?>(null));

        var fetch = new WeClappFetchTriggerNode(A.Fake<ILogger<WeClappFetchTriggerNode>>(), httpClientFactory);
        await fetch.FetchOnceAsync(triggerContext);
        Assert.NotNull(document);

        // --- Phase 2: real data context + real transform/render chain ---
        using var dataContext = new DataContextImpl(JsonDocument.Parse(document.ToJsonString()));
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappToCkNodeConfiguration>())
            .Returns(new WeClappToCkNodeConfiguration
            {
                Mode = "Order",
                Path = "$.item",
                CustomerPath = "$.customer",
                TargetPath = "$.ck",
            });
        A.CallTo(() => nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>())
            .Returns(new DilosRenderNodeConfiguration
            {
                Mode = "AI",
                Submandant = "51696697501",
                Path = "$.item",
                TargetPath = "$.dilos",
            });

        var render = new DilosRenderNode((_, _) => Task.CompletedTask);
        var toCk = new WeClappToCkNode((dc, nc) => render.ProcessObjectAsync(dc, nc));

        await toCk.ProcessObjectAsync(dataContext, nodeContext);

        // --- CK branch: contact data from the customer, computed unit price ---
        var ck = dataContext.Get<CkOrderDocument>("$.ck");
        Assert.NotNull(ck);
        Assert.Equal("TJ Lucas GmbH", ck.Customer.Contact.CompanyName);
        Assert.Equal("Rösrath", ck.Customer.Contact.Address!.CityTown);
        Assert.Equal("5910986621265", ck.Order.OrderNumber);
        Assert.Equal(29.99d, Assert.Single(ck.OrderItems).UnitPriceNet);

        // --- DILOS branch: one AI file content for this order (K* + item P* + shipping P*) ---
        var dilos = dataContext.Get<string>("$.dilos");
        Assert.NotNull(dilos);
        var lines = dilos.TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(3, lines.Length);

        var k = lines[0].Split('|');
        Assert.Equal("K*", k[0]);
        Assert.Equal("7067387625809", k[1]);      // ClientIdnummer = customerNumber
        Assert.Equal("51696697501", k[3]);        // Submandant from pipeline config
        Assert.Equal("5910986621265", k[29]);     // Auftragsnummer1 = WeClapp id
        Assert.Equal("74299", k[30]);             // Auftragsnummer2 = shop number

        var item = lines[1].Split('|');
        Assert.Equal("P*", item[0]);
        Assert.Equal("43222003744925", item[4]);  // Artikelnummer = WeClapp articleId
        Assert.Equal("29.99", item[17]);          // Einzelpreis netto (dot decimal!)

        var shipping = lines[2].Split('|');
        Assert.Equal("-1", shipping[4]);          // shipping cost line marker
        Assert.Equal("4.50", shipping[17]);
    }
}
