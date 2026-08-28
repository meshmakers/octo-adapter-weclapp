using System.Text.Json;
using FakeItEasy;
using Lkv.WeClapp.Core.Dilos;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
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
    /// code: 34 pipe-separated fields on every line, no carriage return anywhere, and a final
    /// 0x0A. Measured on the BYTES that were read, which is why the fixture carries a
    /// .gitattributes entry - without it git would hand a fresh clone CRLF and this assertion
    /// would be the only thing standing between that and a silently wrong delivery.</summary>
    private static void AssertIsADeliverableAsFile(byte[] content)
    {
        Assert.NotEmpty(content);
        Assert.DoesNotContain((byte)'\r', content);
        Assert.Equal((byte)'\n', content[^1]);

        var lines = DilosFile.Encoding.GetString(content).TrimEnd('\n').Split('\n');
        Assert.All(lines, line => Assert.Equal(34, line.Split('|').Length));
        Assert.All(lines, line => Assert.StartsWith("A*|", line, StringComparison.Ordinal));
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
