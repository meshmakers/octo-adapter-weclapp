using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
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
                FileNameTargetPath = "$.dilosFileName",
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
        var lines = dilos.TrimEnd('\n').Split("\n"); // golden AI files are pure LF
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

        // --- File name: AI + Auftragsnummer1 (= WeClapp id, the SAME number as K* field
        //     29 above) — golden precedent AI5910748889425.txt. NOT the shop orderNumber.
        Assert.Equal("AI5910986621265.txt", dataContext.Get<string>("$.dilosFileName"));
    }

    /// <summary>
    /// AS collector chain: WeClappFetch in Batch mode emits ONE execution with ALL
    /// articles; DilosRender renders them into ONE AS content and stamps the golden
    /// Vienna-local file name (golden precedent: one AS file per run, AS20240206020204.txt).
    /// </summary>
    [Fact]
    public async Task WeClappArticles_BatchFlowRendersOneAsFileWithViennaName()
    {
        // --- Phase 1: real fetch node in Batch mode against scripted HTTP ---
        var triggerContext = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => triggerContext.NodeContext).Returns(nodeContext);
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>())
            .Returns(new WeClappFetchTriggerNodeConfiguration
            {
                BaseUrl = "https://demo.weclapp.com/webapp/api/v1",
                ApiKey = "test-key",
                Entity = "article",
                EmitMode = "Batch",
                RetryBackoffBaseSeconds = 0,
            });

        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("""
            {"result":[
              {"id":"43222003744925","name":"Ersatzglas VOLT","articleNumber":"VOLT-EG","unitName":"pc."},
              {"id":"43222003744999","name":"Brille NOVA Größe L","articleNumber":"NOVA-01","unitName":"pc."},
              {"id":"43222003745000","name":"Europalette","articleNumber":"PAL-1","unitName":"pc.",
               "articleType":"LOADING_EQUIPMENT"}
            ]}
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

        // --- Phase 2: real data context + render (fixed clock: 2026-02-05 13:31:34 UTC
        //     = 14:31:34 Vienna/CET) ---
        using var dataContext = new DataContextImpl(JsonDocument.Parse(document.ToJsonString()));
        A.CallTo(() => nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>())
            .Returns(new DilosRenderNodeConfiguration
            {
                Mode = "AS",
                Path = "$.items",
                TargetPath = "$.dilosAs",
                FileNameTargetPath = "$.dilosAsFileName",
            });

        var render = new DilosRenderNode((_, _) => Task.CompletedTask,
            new FixedTimeProvider(new DateTimeOffset(2026, 2, 5, 13, 31, 34, TimeSpan.Zero)));
        await render.ProcessObjectAsync(dataContext, nodeContext);

        var dilos = dataContext.Get<string>("$.dilosAs");
        Assert.NotNull(dilos);
        var lines = dilos.TrimEnd('\n').Split("\n");
        Assert.Equal(2, lines.Length);            // ONE content with ALL articles
        Assert.Equal("43222003744925", lines[0].Split('|')[2]); // f[3] Artikelnummer = WeClapp id
        Assert.Equal("43222003744999", lines[1].Split('|')[2]);

        Assert.Equal("AS20260205143134.txt", dataContext.Get<string>("$.dilosAsFileName"));

        // --- Phase 3: Latin-1 delivery through the node the shipped pipelines use,
        //     SftpUpload@1 configured exactly as the yamls configure it — the umlaut in
        //     "Größe" must land as ONE ISO-8859-1 byte (0xF6), like the golden
        //     Billbee-produced files. The encoding happens while the node builds its upload
        //     stream; the product keeps that step internal (its own suite reaches it through
        //     InternalsVisibleTo), so reflection is the only way to certify the delivered
        //     BYTES from here. A rename turns this test red rather than leaving the byte
        //     assertion quietly unexercised. ---
        var uploadConfiguration = new SftpUploadNodeConfiguration
        {
            ServerConfiguration = "LkvSftp",
            RemoteDirectory = "/",
            FileNamePath = "$.dilosAsFileName",
            Path = "$.dilosAs",
            Encoding = "iso-8859-1",
            OnEncodingError = EncodingErrorHandling.Replace,
        };
        var uploadNode = new SftpUploadNode((_, _) => Task.CompletedTask, A.Fake<IMeshEtlContext>());
        var buildUploadStream = typeof(SftpUploadNode).GetMethod("GetUploadStreamAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buildUploadStream);

        await using var uploadStream = await (Task<Stream>)buildUploadStream!
            .Invoke(uploadNode, [uploadConfiguration, dataContext, nodeContext])!;
        using var uploaded = new MemoryStream();
        await uploadStream.CopyToAsync(uploaded);
        var uploadedBytes = uploaded.ToArray();

        // The name the delivery reads is the one the render wrote — the yaml pins the two
        // paths to each other, this pins the value behind them.
        Assert.Equal("AS20260205143134.txt", dataContext.Get<string>(uploadConfiguration.FileNamePath));
        Assert.Equal(Encoding.Latin1.GetBytes(dilos), uploadedBytes);
        Assert.Contains((byte)0xF6, uploadedBytes); // ö as a single Latin-1 byte, not UTF-8 0xC3 0xB6
    }
}
