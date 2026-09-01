using System.Text.Json;
using FakeItEasy;
using Lkv.WeClapp.Core.Dilos;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// The byte anchor of the DILOS AS article-master delivery. The expected file is the frozen
/// output of the renderer as it stood before the delivery moved onto the product's column
/// renderer, produced from the batch below; it is compared as BYTES in the encoding the delivery
/// actually writes, so a column that moves, a delimiter that changes, a lost trailing newline or
/// a character that stops being Latin-1 all fail here rather than on the customer's import.
///
/// The batch is deliberately not a happy path: it carries a system article that must be dropped,
/// an article whose purchase price has to be selected out of a separate supply-source entity and
/// trimmed to the DILOS number format, an article with no price at all (which must render 0, not
/// empty), an article with no EAN (which must render empty, not 0) and an umlaut.
/// </summary>
public class AsDeliveryParityTests
{
    private const string FixturePath = "tests/AdapterMeshWeClapp.Tests/Fixtures/as-parity-expected.txt";

    // The shape the as pipeline holds after its two paged fetches: raw articles carrying
    // supply-source REFERENCE STUBS, and the separate articleSupplySource entities the prices
    // actually live on.
    private const string Batch = """
        {
          "rawArticles":[
            {"id":"4250","articleNumber":"Default loading equipment","name":"Default loading equipment",
             "unitName":"pc.","articleType":"LOADING_EQUIPMENT","supplySources":[]},
            {"id":"43053033357469","articleNumber":"LE2021540","articleType":"STORABLE",
             "name":"ALT - Lens HAWK (rot/blau polarised)-Glasfarbe:Blau (polarisiert)  - Cat. 3",
             "unitName":"Stk.","ean":"9120103151353","supplySources":[{"articleSupplySourceId":"9001"}]},
            {"id":"43222003744999","articleNumber":"NOVA-01","name":"Brille NOVA Größe L",
             "unitName":"Stk.","articleType":"STORABLE","supplySources":[{"articleSupplySourceId":"9002"}]},
            {"id":"43222003745111","articleNumber":"VOLT-EG","name":"Ersatzglas VOLT",
             "unitName":"Stk.","articleType":"STORABLE","ean":"9120103151360","supplySources":[]}
          ],
          "supplySources":[
            {"id":"9001","articlePrices":[{"price":"1.6200"}]},
            {"id":"9002","articlePrices":[{"price":"35"}]}
          ]
        }
        """;

    [Fact]
    public async Task AsBatch_RendersTheFrozenThirtyFourColumnLayout()
    {
        var expected = await File.ReadAllBytesAsync(RepoFiles.Find(FixturePath));
        AssertIsADeliverableAsFile(expected);

        var produced = await RenderTheShippedAsCompositionAsync();

        Assert.Equal(expected, DilosFile.Encoding.GetBytes(produced));
    }

    /// <summary>Certifies the anchor itself against the invariants every delivered AS file
    /// satisfies, so it cannot silently degrade into a file that merely agrees with the current
    /// code: 34 pipe-separated fields on every line, a CR+LF closing every record and nothing else
    /// carrying a CR or an LF, and a final 0x0D 0x0A. Both halves of the separator are measured
    /// because either half alone is a file LKV's import reads differently from the one intended -
    /// a bare LF leaves a record short of its separator, a bare CR leaks into the first field of
    /// the next one. Measured on the BYTES that were read, which is why the fixture carries a
    /// .gitattributes `-text` entry - without it git normalises the CRs out of the stored blob and
    /// hands every clone whatever its own checkout rule produces, and this assertion would be the
    /// only thing standing between that and a silently wrong delivery.</summary>
    private static void AssertIsADeliverableAsFile(byte[] content)
    {
        Assert.NotEmpty(content);

        var carriageReturns = content.Count(b => b == (byte)'\r');
        var lineFeeds = content.Count(b => b == (byte)'\n');
        Assert.Equal(lineFeeds, carriageReturns);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\n')
            {
                Assert.True(i > 0 && content[i - 1] == (byte)'\r', $"byte {i}: an LF without its CR");
            }

            if (content[i] == (byte)'\r')
            {
                Assert.True(i + 1 < content.Length && content[i + 1] == (byte)'\n',
                    $"byte {i}: a CR without its LF");
            }
        }

        Assert.True(content.Length >= 2 && content[^2] == (byte)'\r' && content[^1] == (byte)'\n',
            "the delivered file ends on 0x0D 0x0A");

        // The trailing separator is dropped rather than split away, so an empty record would be
        // counted instead of silently disappearing.
        var lines = DilosFile.Encoding.GetString(content)[..^2].Split("\r\n");
        Assert.Equal(carriageReturns, lines.Length);
        Assert.All(lines, line => Assert.Equal(34, line.Split('|').Length));
        Assert.All(lines, line => Assert.StartsWith("A*|", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// The empty-batch brake, EXECUTED rather than inspected. A day whose articles are all system
    /// articles (and a tenant bootstrap) renders nothing, and the yaml gates the delivery on the
    /// rendered content being != "" because SftpUpload@1 would otherwise put a 0-byte AS file on
    /// LKV's server and the marker behind it would burn the Vienna day - recoverable only by
    /// deleting the CK entity. Both halves are load-bearing and neither is provable from the yaml
    /// shape: that the renderer writes exactly the empty string for an empty batch (it also has a
    /// trailing-separator option, and "\n" != "" would OPEN the gate), and that If@1 with the
    /// shipped literals then closes. Driven with the real nodes and the shipped configurations.
    /// </summary>
    [Theory]
    [InlineData("""{"rawArticles":[],"supplySources":[]}""", false)]
    [InlineData("""{"rawArticles":[{"id":"4250","articleType":"LOADING_EQUIPMENT"}],"supplySources":[]}""", false)]
    [InlineData("""{"rawArticles":[{"id":"1","name":"B","articleType":"STORABLE"}],"supplySources":[]}""", true)]
    public async Task TheEmptyBatchBrake_OpensOnlyForContent(string document, bool expectDelivery)
    {
        var root = await PipelineDefinitions.DeserializeAsync("weclapp-articles-to-as.yaml");
        var nodes = Walk(root.Transformations).ToList();
        var resolve = Assert.Single(nodes.OfType<WeClappResolveSupplySourcesNodeConfiguration>());
        var render = Assert.Single(nodes.OfType<RenderDelimitedTextNodeConfiguration>());

        // The content brake is the INNER If@1 - the one reading the render's target path. Picking
        // it by that path rather than by position is what keeps this test honest if the yaml grows
        // another gate.
        var brake = Assert.Single(nodes.OfType<IfNodeConfiguration>(),
            gate => gate.Path == render.TargetPath);

        using var dataContext = new DataContextImpl(JsonDocument.Parse(document));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline().RegisterNode<GateProbeNode>();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);

        await new WeClappResolveSupplySourcesNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(dataContext,
                rootContext.RegisterChildNode("WeClappResolveSupplySources", 0, resolve, dataContext));
        await new RenderDelimitedTextNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(dataContext,
                rootContext.RegisterChildNode("RenderDelimitedText", 1, render, dataContext));

        // The shipped gate, with its children swapped for a probe: the literals under test are the
        // yaml's own (path, operator, valueType, value), the delivery itself is not re-run here.
        var probed = brake with
        {
            Transformations = new List<NodeConfiguration>
            {
                new GateProbeNodeConfiguration { TargetPath = "$.probe" },
            },
        };
        await new IfNode(A.Fake<NodeDelegate>()).ProcessObjectAsync(dataContext,
            rootContext.RegisterChildNode("If", 2, probed, dataContext));

        Assert.Equal(expectDelivery ? 1 : null, dataContext.Get<int?>("$.probe"));
    }

    /// <summary>Runs the batch through the nodes the shipped as yaml configures, in the order it
    /// configures them and with ITS configuration values - reading the pipeline definition rather
    /// than restating it is what makes this a test of the delivery instead of a test of the
    /// nodes.</summary>
    private static async Task<string> RenderTheShippedAsCompositionAsync()
    {
        var root = await PipelineDefinitions.DeserializeAsync("weclapp-articles-to-as.yaml");
        var nodes = Walk(root.Transformations).ToList();
        var resolve = Assert.Single(nodes.OfType<WeClappResolveSupplySourcesNodeConfiguration>());
        var render = Assert.Single(nodes.OfType<RenderDelimitedTextNodeConfiguration>());

        using var dataContext = new DataContextImpl(JsonDocument.Parse(Batch));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline();
        var rootContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);

        await new WeClappResolveSupplySourcesNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(dataContext,
                rootContext.RegisterChildNode("WeClappResolveSupplySources", 0, resolve, dataContext));

        await new RenderDelimitedTextNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(dataContext,
                rootContext.RegisterChildNode("RenderDelimitedText", 1, render, dataContext));

        var produced = dataContext.Get<string>(render.TargetPath);
        Assert.False(string.IsNullOrEmpty(produced),
            $"the composition wrote nothing to '{render.TargetPath}'");
        return produced!;
    }
}
