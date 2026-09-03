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
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// End-to-end chain over the custom nodes with a REAL pipeline data context (DataContextImpl, as
/// the platform's own full-chain tests use): the document the shipped pipelines seed →
/// WeClappToCk → DilosRender → AI lines, and the article batch → DilosExportRunKey →
/// WeClappResolveSupplySources → RenderDelimitedText → AS content → SftpUpload@1 bytes. The
/// seeding itself is the product's MakeHttpRequest@1 and is not re-tested here; what the chain
/// must agree on is the document SHAPE, so the fixtures below carry exactly the paths the yamls
/// configure ($.current and $.customerResponse.result[0] for the order chain, $.rawArticles +
/// $.supplySources for the article batch - $.items is the preparation step's OUTPUT, not a seeded
/// path). The platform built-ins (GetOrCreate/ApplyChanges) need a repository and are exercised in
/// the tenant spike instead.
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
                "customerId":"7","orderDate":1707177600000,"grossAmount":"41.39",
                "deliveryAddress":{"company":"TJ Lucas","countryCode":"DE","zipcode":"51503",
                                   "street1":"Im Wielputzfeld 15a","city":"Rösrath"},
                "orderItems":[{"positionNumber":1,"articleId":"43222003744925",
                               "quantity":"1","netAmount":"29.99","grossAmount":"35.99",
                               "taxId":"3681","title":"Ersatzglas VOLT"}],
                "shippingCostItems":[{"netAmount":"4.50","grossAmount":"5.40","taxId":"3681",
                                      "title":"DHL Standard (DE)"}]
              },
              "customerResponse":{"result":[
                {"id":"7","customerNumber":"7067387625809","company":"TJ Lucas GmbH",
                 "email":"tj@example.com",
                 "addresses":[{"street1":"Im Wielputzfeld 15a","zipcode":"51503",
                               "city":"Rösrath","countryCode":"DE"}]}
              ]},
              "taxes":[{"id":"3681","name":"AT Umsatzsteuer","taxValue":"20"}]
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
                TaxesPath = "$.taxes",
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
        // AI stays LF on purpose - the AS/AI separator split is contractual, not an oversight.
        Assert.DoesNotContain('\r', dilos);
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
        Assert.Equal("20", item[15]);             // MwSt in whole percent, joined from $.taxes
        Assert.Equal("29.99", item[17]);          // Einzelpreis netto (dot decimal!)
        Assert.Equal("29.99", item[18]);          // Positionspreis netto
        Assert.Equal("35.99", item[19]);          // Einzelpreis brutto
        Assert.Equal("35.99", item[20]);          // Positionspreis brutto

        var shipping = lines[2].Split('|');
        Assert.Equal("-1", shipping[4]);          // shipping cost line marker
        Assert.Equal("20", shipping[15]);
        Assert.Equal("4.50", shipping[17]);
        Assert.Equal("4.50", shipping[18]);
        Assert.Equal("5.40", shipping[19]);
        Assert.Equal("5.40", shipping[20]);

        // --- File name: AI + Auftragsnummer1 (= WeClapp id, the SAME number as K* field
        //     29 above) — golden precedent AI5910748889425.txt. NOT the shop orderNumber.
        // The name matches a golden file, and only the NAME does: that file is the previous shop
        // connector's output (18 and 20 duplicated, 16/19/21 empty) and is never the reference for
        // what the price fields carry - the assertions above are.
        Assert.Equal("AI5910986621265.txt", dataContext.Get<string>("$.dilosFileName"));
    }

    /// <summary>
    /// AS delivery chain, driven by the SHIPPED yaml's own node configurations: the export-run key
    /// stamps the day and the file name from one Vienna clock read, the preparation step drops the
    /// system article and projects the EK-Preis, the product's column node renders the 34-column
    /// document, and SftpUpload@1 encodes it. What this adds over the byte anchor in
    /// AsDeliveryParityTests is the two ends the anchor does not reach: the NAME the delivery is
    /// given, and the BYTES that leave the process.
    /// </summary>
    [Fact]
    public async Task WeClappArticles_BatchRendersOneAsFileWithViennaName()
    {
        // The shape the as pipeline holds after its two paged fetches. The loading equipment is
        // part of it on purpose: the delivery must drop system articles.
        const string document = """
            {
              "rawArticles":[
                {"id":"43222003744925","name":"Ersatzglas VOLT","articleNumber":"VOLT-EG",
                 "unitName":"pc.","articleType":"STORABLE","supplySources":[]},
                {"id":"43222003744999","name":"Brille NOVA Größe L","articleNumber":"NOVA-01",
                 "unitName":"pc.","articleType":"STORABLE","supplySources":[]},
                {"id":"43222003745000","name":"Europalette","articleNumber":"PAL-1","unitName":"pc.",
                 "articleType":"LOADING_EQUIPMENT","supplySources":[]}
              ],
              "supplySources":[]
            }
            """;

        var root = await PipelineDefinitions.DeserializeAsync("weclapp-articles-to-as.yaml");
        var nodes = Walk(root.Transformations).ToList();
        var exportRunKey = Assert.Single(nodes.OfType<DilosExportRunKeyNodeConfiguration>());
        var resolve = Assert.Single(nodes.OfType<WeClappResolveSupplySourcesNodeConfiguration>());
        var render = Assert.Single(nodes.OfType<RenderDelimitedTextNodeConfiguration>());
        var upload = Assert.Single(nodes.OfType<SftpUploadNodeConfiguration>());

        using var dataContext = new DataContextImpl(JsonDocument.Parse(document));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);

        Task Step(IPipelineNode node, string name, uint index, NodeConfiguration configuration) =>
            node.ProcessObjectAsync(dataContext,
                rootContext.RegisterChildNode(name, index, configuration, dataContext));

        // Fixed clock: 2026-02-05 13:31:34 UTC = 14:31:34 Vienna (CET).
        await Step(new DilosExportRunKeyNode((_, _) => Task.CompletedTask,
                new FixedTimeProvider(new DateTimeOffset(2026, 2, 5, 13, 31, 34, TimeSpan.Zero))),
            "DilosExportRunKey", 0, exportRunKey);
        await Step(new WeClappResolveSupplySourcesNode((_, _) => Task.CompletedTask),
            "WeClappResolveSupplySources", 1, resolve);
        await Step(new RenderDelimitedTextNode((_, _) => Task.CompletedTask),
            "RenderDelimitedText", 2, render);

        var dilos = dataContext.Get<string>(render.TargetPath);
        Assert.NotNull(dilos);
        // Split on the CR+LF the AS article master is contracted with, not on the bare LF: on a
        // document whose records are separated the wrong way this splits into one line instead of
        // two and says so, where splitting on "\n" would count the same two lines either way and
        // only leave a stray CR on the last field of each. The trailing separator is asserted
        // before it is sliced off, so a document that lost it (or rendered nothing at all) fails
        // saying exactly that instead of dying inside the slice.
        Assert.EndsWith("\r\n", dilos, StringComparison.Ordinal);
        var lines = dilos[..^2].Split("\r\n");
        Assert.Equal(2, lines.Length);                          // ONE document, system article dropped
        Assert.Equal("43222003744925", lines[0].Split('|')[2]); // DILOS field 3 = Artikelnummer
        Assert.Equal("43222003744999", lines[1].Split('|')[2]);

        // The name the delivery reads is the one the export-run node wrote, from the same clock
        // read as the marker day - the yaml pins the two paths to each other, this pins the value
        // behind them.
        Assert.Equal("AS20260205143134.txt", dataContext.Get<string>(upload.FileNamePath!));

        // Latin-1 delivery through the node the shipped pipelines use, configured exactly as the
        // yaml configures it - the umlaut in "Größe" must land as ONE ISO-8859-1 byte (0xF6), like
        // the golden Billbee-produced files. The encoding happens while the node builds its upload
        // stream; the product keeps that step internal (its own suite reaches it through
        // InternalsVisibleTo), so reflection is the only way to certify the delivered BYTES from
        // here. A rename turns this test red rather than leaving the byte assertion quietly
        // unexercised.
        var uploadNode = CreateSftpUploadNode();
        var buildUploadStream = typeof(SftpUploadNode).GetMethod("GetUploadStreamAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buildUploadStream);

        var uploadContext = rootContext.RegisterChildNode("SftpUpload", 3, upload, dataContext);
        await using var uploadStream = await (Task<Stream>)buildUploadStream!
            .Invoke(uploadNode, [upload, dataContext, uploadContext])!;
        using var uploaded = new MemoryStream();
        await uploadStream.CopyToAsync(uploaded, TestContext.Current.CancellationToken);
        var uploadedBytes = uploaded.ToArray();

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
