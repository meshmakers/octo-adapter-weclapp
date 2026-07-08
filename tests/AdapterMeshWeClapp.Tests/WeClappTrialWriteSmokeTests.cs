using System.Globalization;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Environment-gated LIVE dry-run smoke for the AR/BE write-back against the WeClapp TRIAL
/// account: runs only when WECLAPP_TRIAL_API_KEY and WECLAPP_TRIAL_BASEURL are set and is a
/// no-op otherwise. Uses DryRun exclusively — the AR PUTs go out with ?dryRun=true (WeClapp
/// validates without persisting), BE plans movements but posts nothing. Validates live what
/// unit tests cannot: the GET→merge→full-PUT round trip (partial bodies are rejected with
/// 400, parcel add/remove with 409 — both found by this smoke), the bare-object id-GET
/// envelope, the warehouse defaultStoragePlaceId field name, and the bulk-read query forms.
/// Logs ids/counts only.
/// NEVER point these at WECLAPP_CUSTOMER_* — the customer system is productive (GET only).
/// </summary>
public class WeClappTrialWriteSmokeTests(ITestOutputHelper output)
{
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);

    private static (string BaseUrl, string ApiKey)? LiveConfig()
    {
        var apiKey = Env("WECLAPP_TRIAL_API_KEY");
        var baseUrl = Env("WECLAPP_TRIAL_BASEURL");
        return string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl)
            ? null
            : (baseUrl, apiKey);
    }

    private static HttpClient LiveHttpClient() => new(
        new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });

    private static IHttpClientFactory LiveHttpClientFactory()
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).Returns(LiveHttpClient());
        return factory;
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) FakePipeline(
        string fileName, string content)
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = A.Fake<INodeContext>();
        var next = A.Fake<NodeDelegate>();
        A.CallTo(() => dataContext.Get<string>("$.fileName")).Returns(fileName);
        A.CallTo(() => dataContext.Get<string>("$.content")).Returns(content);
        A.CallTo(() => nodeContext.Info(A<string>._)).Invokes(call =>
            output.WriteLine($"  node: {call.Arguments[0]}"));
        A.CallTo(() => nodeContext.Error(A<string>._)).Invokes(call =>
            output.WriteLine($"  node ERROR: {call.Arguments[0]}"));
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task ArWrite_LiveDryRun_ValidatesFullPutRoundTripAgainstRealShipment()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_TRIAL_API_KEY / WECLAPP_TRIAL_BASEURL not set.");
            return;
        }

        // Pick a real, reusable order-linked shipment so the UpdateExisting path fires the
        // PUTs. Items are optional: trial shipments carry either salesOrderId or items,
        // never both (verified 2026-07-07) — the PUT-shape validation works without them,
        // the item-quantity echo is then simply not exercised.
        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);
        var shipmentsBody = WeClappApi.EnsureSuccess(
            await api.GetAsync("shipment?pageSize=10"), "GET shipments");
        var candidate = JsonNode.Parse(shipmentsBody)?["result"]?.AsArray()?.OfType<JsonNode>()
            .FirstOrDefault(s =>
                s["status"]?.ToString() != "CANCELLED" &&
                !string.IsNullOrEmpty(s["salesOrderId"]?.ToString()));
        if (candidate is null)
        {
            output.WriteLine("SKIPPED: trial has no order-linked shipment.");
            return;
        }

        var orderId = candidate["salesOrderId"]!.ToString();
        var shipmentItemCount = candidate["shipmentItems"]?.AsArray()?.Count ?? 0;

        // Take the article id from the order so the synthetic AR stays consistent with it
        // (id GETs return the bare object — no result wrapper).
        var orderBody = WeClappApi.EnsureSuccess(
            await api.GetAsync($"salesOrder/id/{orderId}"), $"GET salesOrder {orderId}");
        var articleId = JsonNode.Parse(orderBody)?["orderItems"]?.AsArray()?.OfType<JsonNode>()
                            .Select(i => i["articleId"]?.ToString())
                            .FirstOrDefault(id => !string.IsNullOrEmpty(id))
                        ?? "999999"; // no order item on the trial: match warning expected, PUT still valid
        var quantity = "1";
        var today = DateTime.UtcNow.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        // Synthetic AR for that real order (golden field counts; carrier 9 → AUSTRIAN_POST
        // fallback, one carrier-list GET — the trial has no shippingCarrier entities).
        var arContent =
            $"K*|1|1|SMOKE||{orderId}|SMOKE-2|0|0|0|{today}|1|1|2,5\r\n" +
            $"C*|{orderId}|9|LKVSMOKETRACK01|Karton|Standard|2,5\r\n" +
            $"P*|{orderId}|1|{articleId}||||||0|{quantity}|0\r\n" +
            $"L*|{orderId}|1|{articleId}||||||{quantity}|LKVSMOKETRACK01\r\n";

        var config = new WeClappArWriteNodeConfiguration
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            DryRun = true, // NEVER flip in this smoke — dry-run validation only
        };
        var (dataContext, nodeContext, next) = FakePipeline("SMOKE-AR.TXT", arContent);
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>()).Returns(config);

        var node = new WeClappArWriteNode(next, A.Fake<ILogger<WeClappArWriteNode>>(), LiveHttpClientFactory());
        await node.ProcessObjectAsync(dataContext, nodeContext); // throws if WeClapp rejects the PUT shape

        output.WriteLine($"LIVE AR dry-run validated: order {orderId}, shipment {candidate["id"]} " +
                         $"({shipmentItemCount} items), full GET→merge→PUT round trip " +
                         "(data + status SHIPPED) accepted with dryRun=true");
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BeWrite_LiveDryRun_ValidatesBulkReadsAndWarehouseShape()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_TRIAL_API_KEY / WECLAPP_TRIAL_BASEURL not set.");
            return;
        }

        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);
        var warehousesBody = WeClappApi.EnsureSuccess(await api.GetAsync("warehouse"), "GET warehouse");
        var warehouse = JsonNode.Parse(warehousesBody)?["result"]?.AsArray()?.OfType<JsonNode>().FirstOrDefault();
        if (warehouse is null)
        {
            output.WriteLine("SKIPPED: trial has no warehouse.");
            return;
        }

        var warehouseId = warehouse["id"]!.ToString();
        // Field-name proof for the incoming-booking target (research: Main 4239 → place 4244).
        var defaultPlace = warehouse["defaultStoragePlaceId"]?.ToString();
        Assert.False(string.IsNullOrEmpty(defaultPlace),
            "warehouse.defaultStoragePlaceId must exist — incoming bookings target it");

        var articlesBody = WeClappApi.EnsureSuccess(
            await api.GetAsync("article?page=1&pageSize=1&properties=id"), "GET article");
        var articleId = JsonNode.Parse(articlesBody)?["result"]?.AsArray()?.FirstOrDefault()?["id"]?.ToString();
        if (string.IsNullOrEmpty(articleId))
        {
            output.WriteLine("SKIPPED: trial has no articles.");
            return;
        }

        var config = new WeClappBeWriteNodeConfiguration
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            WarehouseId = warehouseId,
            DryRun = true, // NEVER flip in this smoke — movements must not be booked
        };
        var (dataContext, nodeContext, next) = FakePipeline(
            "SMOKE-BE.txt", $"{articleId}|0|0||1|VER\r\n");
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>()).Returns(config);

        var node = new WeClappBeWriteNode(next, A.Fake<ILogger<WeClappBeWriteNode>>(), LiveHttpClientFactory());
        await node.ProcessObjectAsync(dataContext, nodeContext); // throws if any bulk-read query is wrong

        output.WriteLine($"LIVE BE dry-run validated: warehouse {warehouseId} " +
                         $"(default place {defaultPlace}), article {articleId} planned without booking");
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}
