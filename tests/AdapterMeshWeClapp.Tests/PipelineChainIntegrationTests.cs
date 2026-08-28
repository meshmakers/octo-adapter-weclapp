using System.Reflection;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// End-to-end chain over the custom nodes with a REAL pipeline data context (DataContextImpl, as
/// the platform's own full-chain tests use): the document the shipped pipelines seed →
/// WeClappToCk → DilosRender → AI lines, and the article batch → DilosRender → AS content →
/// SftpUpload@1 bytes. The seeding itself is the product's MakeHttpRequest@1 and is not re-tested
/// here; what the chain must agree on is the document SHAPE, so the fixtures below carry exactly
/// the paths the yamls configure ($.current and $.customerResponse.result[0] for the order chain,
/// $.items for the article batch). The platform built-ins (GetOrCreate/ApplyChanges) need a
/// repository and are exercised in the tenant spike instead.
/// </summary>
public class PipelineChainIntegrationTests
{
    [Fact]
    public async Task WeClappOrder_FlowsThroughCkAndDilosRenderToAiLines()
    {
        // --- Phase 1: the per-order document weclapp-orders-to-ai.yaml builds. The order is one
        //     flat element of the fetched array (ForEach@1 keyPath $.current), the customer the
        //     single match of the id-eq lookup, which lands as the raw response body. ---
        const string document = """
            {
              "current":{
                "id":"5910986621265","orderNumber":"74299","customerNumber":"7067387625809",
                "customerId":"7","orderDate":1707177600000,
                "deliveryAddress":{"company":"TJ Lucas","countryCode":"DE","zipcode":"51503",
                                   "street1":"Im Wielputzfeld 15a","city":"Rösrath"},
                "orderItems":[{"positionNumber":1,"articleId":"43222003744925",
                               "quantity":"1","netAmount":"29.99","title":"Ersatzglas VOLT"}],
                "shippingCostItems":[{"netAmount":"4.50","title":"DHL Standard (DE)"}]
              },
              "customerResponse":{"result":[
                {"id":"7","customerNumber":"7067387625809","company":"TJ Lucas GmbH",
                 "email":"tj@example.com",
                 "addresses":[{"street1":"Im Wielputzfeld 15a","zipcode":"51503",
                               "city":"Rösrath","countryCode":"DE"}]}
              ]}
            }
            """;

        // --- Phase 2: real data context + real transform/render chain ---
        var nodeContext = A.Fake<INodeContext>();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(document));
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappToCkNodeConfiguration>())
            .Returns(new WeClappToCkNodeConfiguration
            {
                Mode = "Order",
                Path = "$.current",
                CustomerPath = "$.customerResponse.result[0]",
                TargetPath = "$.ck",
            });
        A.CallTo(() => nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>())
            .Returns(new DilosRenderNodeConfiguration
            {
                Mode = "AI",
                Submandant = "51696697501",
                Path = "$.current",
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
    /// AS collector chain: the article batch the as pipeline assembles renders into ONE AS content
    /// with the golden Vienna-local file name (golden precedent: one AS file per run,
    /// AS20240206020204.txt), and the delivery node writes it as Latin-1.
    /// </summary>
    [Fact]
    public async Task WeClappArticles_BatchRendersOneAsFileWithViennaName()
    {
        // --- Phase 1: the batch weclapp-articles-to-as.yaml holds at $.items after the fetch and
        //     the supply-source resolution. The loading equipment is part of it on purpose: the
        //     delivery must drop system articles. ---
        const string document = """
            {"items":[
              {"id":"43222003744925","name":"Ersatzglas VOLT","articleNumber":"VOLT-EG","unitName":"pc."},
              {"id":"43222003744999","name":"Brille NOVA Größe L","articleNumber":"NOVA-01","unitName":"pc."},
              {"id":"43222003745000","name":"Europalette","articleNumber":"PAL-1","unitName":"pc.",
               "articleType":"LOADING_EQUIPMENT"}
            ]}
            """;

        // --- Phase 2: real data context + render (fixed clock: 2026-02-05 13:31:34 UTC
        //     = 14:31:34 Vienna/CET) ---
        var nodeContext = A.Fake<INodeContext>();
        using var dataContext = new DataContextImpl(JsonDocument.Parse(document));
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
        var uploadNode = CreateSftpUploadNode();
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

    /// <summary>
    /// Builds the product's upload node without naming its constructor. That constructor is not
    /// a stable contract — the product adds services to it, and a build against an SDK newer
    /// than the one on nuget.org then fails to COMPILE this file (CS7036 on CI, green locally,
    /// 24.08.) over services this test never reaches. So the ctor is resolved at runtime and
    /// only the two parameters this test actually feeds are matched by type. Anything else the
    /// SDK adds gets a STRICT fake: silent while the upload-stream path ignores it, and a loud
    /// FakeItEasy ExpectationException the day that path starts calling it — never a quietly
    /// wrong byte assertion.
    /// </summary>
    private static SftpUploadNode CreateSftpUploadNode()
    {
        var ctor = Assert.Single(typeof(SftpUploadNode).GetConstructors());

        var arguments = ctor.GetParameters().Select(object (parameter) =>
            parameter.ParameterType == typeof(NodeDelegate)
                ? (NodeDelegate)((_, _) => Task.CompletedTask)
                : parameter.ParameterType == typeof(IMeshEtlContext)
                    ? A.Fake<IMeshEtlContext>()
                    : FakeItEasy.Sdk.Create.Fake(parameter.ParameterType,
                        options => options.Strict())).ToArray();

        return (SftpUploadNode)ctor.Invoke(arguments);
    }
}
