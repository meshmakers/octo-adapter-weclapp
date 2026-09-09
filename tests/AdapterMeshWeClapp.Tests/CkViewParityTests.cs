using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// The CK-shaped view ($.ck) is the ONLY thing the persistence nodes read, so "the same CK updates"
/// reduces to "the same $.ck view for the same input" - as long as GetOrCreate/CreateUpdateInfo keep
/// their configuration, which the yaml contract tests pin. The fixtures were frozen from
/// WeClappToCk@1 at ca0cecb, the last commit that shipped the node, over the documents below, and
/// restricted to the attributes the shipped yamls persist - read off the yamls' own CreateUpdateInfo@1
/// nodes, so an attribute cannot come or go without the fixture noticing. Everything else the node
/// computed (contact, address, delivery date, order items) is not part of the contract and is tracked
/// under AB#4228.
/// </summary>
public class CkViewParityTests
{
    private const string ArticleFixture = "tests/AdapterMeshWeClapp.Tests/Fixtures/ck-view-article.json";
    private const string OrderFixture = "tests/AdapterMeshWeClapp.Tests/Fixtures/ck-view-order.json";

    // The WeClapp samples the core parser tests already read - one place, no second copy.
    private const string Samples = "tests/Lkv.WeClapp.Core.Tests/Fixtures";

    private static readonly JsonSerializerOptions FixtureJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",                                  // frozen on Windows, compared on Linux too
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- documents in the shape the loop body sees: $.current, and for ai $.customerResponse ----

    private static IEnumerable<(string Name, string Document)> ArticleCases()
    {
        yield return ("contract-168914-with-ean", """
            {"current":{"id":"168914","articleNumber":"TW_Z_074","name":"Ersatz Schnellverschlüsse",
             "articleType":"STORABLE","ean":"9001234567890","active":true}}
            """);
        yield return ("sample-4262-without-ean", Wrap(("current", SampleEntity("article.json", "4262"))));
        // Loading equipment leaves no view at all - the loop body ends for it.
        yield return ("sample-4250-loading-equipment", Wrap(("current", SampleEntity("article.json", "4250"))));
    }

    private static IEnumerable<(string Name, string Document)> OrderCases()
    {
        yield return ("chain-5910986621265-company", """
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
            """);
        yield return ("b2c-622075-person", """
            {"current":{"id":"622075","orderNumber":"SO-1001","customerNumber":"K-77","orderDate":1782820560333,
              "orderItems":[]},
             "customerResponse":{"result":[
              {"id":"77","customerNumber":"K-77","company":"","firstName":"Erika","lastName":"Muster"}]}}
            """);
        yield return ("sample-4274-company-no-items", Wrap(
            ("current", SampleEntity("salesOrder.json", "4274")),
            ("customerResponse", new JsonObject { ["result"] = new JsonArray(SampleEntity("customer.json", "4269")) })));
        // The system debitor leaves no view at all.
        yield return ("anonymous-debitor", """
            {"current":{"id":"1","orderNumber":"","customerNumber":"ANONYMOUS_DEBITOR","orderDate":0,"orderItems":[]},
             "customerResponse":{"result":[{"id":"3456","customerNumber":"ANONYMOUS_DEBITOR","company":"ANONYMOUS_COMPANY"}]}}
            """);
    }

    // ---- the two parity facts ----

    [Fact]
    public async Task ArticleView_MatchesTheFrozenFixture()
    {
        var actual = new JsonObject();
        foreach (var (name, document) in ArticleCases())
        {
            actual[name] = await RunAndProjectAsync("weclapp-articles-to-ck.yaml", document);
        }

        await AssertMatchesFixtureAsync(ArticleFixture, actual);
    }

    [Fact]
    public async Task OrderView_MatchesTheFrozenFixture()
    {
        var actual = new JsonObject();
        foreach (var (name, document) in OrderCases())
        {
            actual[name] = await RunAndProjectAsync("weclapp-orders-to-ai.yaml", document);
        }

        await AssertMatchesFixtureAsync(OrderFixture, actual);
    }

    // ---- producer: the shipped mapping chain; projection: what the shipped yaml persists ----

    // The view holds exactly the attributes the yaml's CreateUpdateInfo@1 nodes read off $.ck, keyed by
    // attribute name and grouped by the node's path (the ck yaml persists FLAT at $.ck, the ai yaml
    // under $.ck.Customer and $.ck.Order). Reading that list off the yaml instead of restating it is
    // what makes the fixture a change detector: an attribute added to or dropped from the yaml shows
    // up here, and an attribute the yaml stopped persisting cannot keep claiming parity.
    private static async Task<JsonNode?> RunAndProjectAsync(string yamlFileName, string document)
    {
        var dataContext = await ShippedMappingChain.RunAsync(yamlFileName, document);
        if (!dataContext.Exists("$.ck"))
        {
            return null;
        }

        var root = await PipelineDefinitions.DeserializeAsync(yamlFileName);
        var updates = Walk(root.Transformations).OfType<CreateUpdateInfoNodeConfiguration>()
            .Where(update => update.Path is "$.ck" || update.Path?.StartsWith("$.ck.", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(updates);

        var view = new JsonObject();
        foreach (var update in updates)
        {
            var group = view;
            if (update.Path != "$.ck")
            {
                var name = update.Path!["$.ck.".Length..];
                if (view[name] is not JsonObject nested)
                {
                    nested = new JsonObject();
                    view[name] = nested;
                }

                group = nested;
            }

            foreach (var attribute in update.AttributeUpdates ?? [])
            {
                CopyIfPresent(group, Assert.IsType<string>(attribute.AttributeName), dataContext,
                    Assert.IsType<string>(attribute.ValuePath));
            }
        }

        return view;
    }

    // Absent and JSON null are DIFFERENT things to CreateUpdateInfo@1 - absent means no update, null
    // clears the attribute - so the view keeps them apart: an absent field is left out, a null one
    // is written as null.
    private static void CopyIfPresent(JsonObject target, string name, IDataContext dataContext, string path)
    {
        var kind = dataContext.GetKind(path);
        if (kind == DataKind.Undefined)
        {
            return;
        }

        target[name] = kind == DataKind.Null ? null : dataContext.Get<JsonNode>(path);
    }

    private static async Task AssertMatchesFixtureAsync(string fixture, JsonObject actual)
    {
        var file = RepoFiles.Find(fixture);
        var rendered = actual.ToJsonString(FixtureJson) + "\n";

        var expected = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        // A checkout may have turned the LF fixture into CRLF; the comparison is about content.
        Assert.Equal(expected.Replace("\r\n", "\n"), rendered);
    }

    private static JsonNode SampleEntity(string fileName, string id)
    {
        var json = File.ReadAllText(RepoFiles.Find($"{Samples}/{fileName}"));
        var result = JsonNode.Parse(json)!["result"]!.AsArray();
        return result.Single(entity => entity!["id"]!.GetValue<string>() == id)!.DeepClone();
    }

    private static string Wrap(params (string Key, JsonNode Value)[] parts)
    {
        var document = new JsonObject();
        foreach (var (key, value) in parts)
        {
            document[key] = value;
        }

        return document.ToJsonString();
    }
}
