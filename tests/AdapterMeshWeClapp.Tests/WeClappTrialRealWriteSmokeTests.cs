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
/// DOUBLE-GATED live smoke that REALLY WRITES to the WeClapp TRIAL account: requires
/// WECLAPP_TRIAL_API_KEY + WECLAPP_TRIAL_BASEURL and additionally the explicit opt-in
/// WECLAPP_TRIAL_REAL_WRITE=1 (set process-scoped for one run only — NEVER persist it),
/// so a normal `dotnet test` never mutates the trial. Proves what dry-run cannot:
/// the real createShipment (+ its response envelope via the tolerant unwrap), the real
/// SHIPPED persist with read-back, and real BE movement bookings including the
/// idempotent re-run (delta 0) and the outgoing booking that restores the stock.
/// NEVER point these at WECLAPP_CUSTOMER_* — the customer system is productive (GET only).
/// </summary>
public class WeClappTrialRealWriteSmokeTests(ITestOutputHelper output)
{
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);

    private static (string BaseUrl, string ApiKey)? RealWriteConfig()
    {
        // Opt-in gate is process/user env "1" — checked FIRST so the default run is a no-op.
        if (Env("WECLAPP_TRIAL_REAL_WRITE") != "1")
        {
            return null;
        }

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

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next, List<string> Infos)
        FakePipeline(string fileName, string content)
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = A.Fake<INodeContext>();
        var next = A.Fake<NodeDelegate>();
        var infos = new List<string>();
        A.CallTo(() => dataContext.Get<string>("$.fileName")).Returns(fileName);
        A.CallTo(() => dataContext.Get<string>("$.content")).Returns(content);
        A.CallTo(() => nodeContext.Info(A<string>._, A<object[]>._)).Invokes(call =>
        {
            var message = NodeLogCapture.Format(call);
            infos.Add(message);
            output.WriteLine($"  node: {message}");
        });
        A.CallTo(() => nodeContext.Error(A<string>._, A<object[]>._)).Invokes(call =>
            output.WriteLine($"  node ERROR: {NodeLogCapture.Format(call)}"));
        return (dataContext, nodeContext, next, infos);
    }

    [Fact]
    public async Task ArWrite_RealWrite_CreatesShipmentMarksShippedAndReadsBack()
    {
        if (RealWriteConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: real-write gate not open (WECLAPP_TRIAL_REAL_WRITE=1 + trial envs required).");
            return;
        }

        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);

        // Self-contained arrange: create a fresh order (existing orders stay untouched —
        // they are the dry-run smoke's fixtures). Trial-proven constraints baked in:
        // createShipment needs an order WITH items in status "Auftragsbestätigung erstellt"
        // (400: 'Shipment creation is only possible for sales orders with the status
        // "Sales order confirmation created"'; 400 'Sales order has no items' on empty ones).
        var storableArticleId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("article?page=1&pageSize=50&properties=id,articleType"), "GET article"))!
            ["result"]!.AsArray().OfType<JsonNode>()
            .FirstOrDefault(a => a["articleType"]?.ToString() == "STORABLE")?["id"]?.ToString();
        var customerId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("salesOrder?pageSize=1"), "GET salesOrders"))!["result"]!.AsArray()
            .FirstOrDefault()?["customerId"]?.ToString();
        if (string.IsNullOrEmpty(storableArticleId) || string.IsNullOrEmpty(customerId))
        {
            output.WriteLine("SKIPPED: trial lacks a STORABLE article or an order to borrow a customer from.");
            return;
        }

        var articleId = storableArticleId;

        // createShipment only derives shipment items for articles WITH available stock
        // (trial-proven: 0 stock → shipment without items → SHIPPED rejected with
        // "status update not possible"). Ensure stock before creating the order.
        var stockRows = JsonNode.Parse(WeClappApi.EnsureSuccess(
            await api.GetAsync($"warehouseStock?articleId-eq={articleId}"), "GET stock"))!["result"]!.AsArray();
        var stock = stockRows.OfType<JsonNode>()
            .Sum(r => decimal.Parse(r["quantity"]?.ToString() ?? "0", CultureInfo.InvariantCulture));
        if (stock < 1)
        {
            var place = JsonNode.Parse(WeClappApi.EnsureSuccess(
                    await api.GetAsync("warehouse"), "GET warehouse"))!["result"]!.AsArray().OfType<JsonNode>()
                .Select(w => w["defaultStoragePlaceId"]?.ToString())
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));
            WeClappApi.EnsureSuccess(await api.SendAsync(HttpMethod.Post,
                    "warehouseStockMovement/bookIncomingMovement", new JsonObject
                    {
                        ["articleId"] = articleId,
                        ["quantity"] = "5",
                        ["targetStoragePlaceId"] = place,
                        ["movementNote"] = "real-write smoke arrange",
                    }),
                "arrange stock");
            output.WriteLine($"  arranged: article {articleId} stocked up (+5, was {stock})");
        }

        var createOrderBody = new JsonObject
        {
            ["customerId"] = customerId,
            ["orderItems"] = new JsonArray(new JsonObject
            {
                ["articleId"] = articleId,
                ["quantity"] = "1",
            }),
        };
        var createdOrder = JsonNode.Parse(WeClappApi.EnsureSuccess(
            await api.SendAsync(HttpMethod.Post, "salesOrder", createOrderBody), "POST salesOrder"));
        var orderId = (createdOrder?["result"] ?? createdOrder)?["id"]?.ToString()
                      ?? throw new InvalidOperationException("POST salesOrder returned no id");
        output.WriteLine($"  arranged: order {orderId} created (customer {customerId}, article {articleId})");

        var orderFull = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync($"salesOrder/id/{orderId}"), $"GET salesOrder {orderId}")) as JsonObject
                        ?? throw new InvalidOperationException("salesOrder id-GET returned no object");
        orderFull["status"] = "ORDER_CONFIRMATION_PRINTED";
        WeClappApi.EnsureSuccess(
            await api.SendAsync(HttpMethod.Put, $"salesOrder/id/{orderId}", orderFull),
            $"PUT salesOrder {orderId} status");
        output.WriteLine($"  arranged: order {orderId} lifted to ORDER_CONFIRMATION_PRINTED");

        var today = DateTime.UtcNow.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var arContent =
            $"K*|1|1|REAL||{orderId}|REAL-2|0|0|0|{today}|1|1|2,5\r\n" +
            $"C*|{orderId}|9|LKVREAL0001|Karton|Standard|2,5\r\n" +
            $"P*|{orderId}|1|{articleId}||||||0|1|0\r\n" +
            $"L*|{orderId}|1|{articleId}||||||1|LKVREAL0001\r\n";

        var config = new WeClappArWriteNodeConfiguration
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            DryRun = false, // REAL write — trial only, double-gated
        };
        var (dataContext, nodeContext, next, _) = FakePipeline("REAL-AR.TXT", arContent);
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>()).Returns(config);

        var node = new WeClappArWriteNode(next, A.Fake<ILogger<WeClappArWriteNode>>(), LiveHttpClientFactory(),
            A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext>());
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Read-back: the order now has exactly one shipment, SHIPPED, with our tracking
        // and the delivered item derived from the order.
        var readBack = JsonNode.Parse(WeClappApi.EnsureSuccess(
            await api.GetAsync($"shipment?salesOrderId-eq={orderId}"), "read-back"))!["result"]!.AsArray();
        var shipment = Assert.Single(readBack.OfType<JsonNode>());
        Assert.Equal("SHIPPED", shipment["status"]!.ToString());
        Assert.Equal("LKVREAL0001", shipment["packageTrackingNumber"]!.ToString());
        Assert.False(string.IsNullOrEmpty(shipment["shippingDate"]?.ToString()), "shippingDate must be set");
        var shippedItem = Assert.Single(shipment["shipmentItems"]!.AsArray().OfType<JsonNode>());
        Assert.Equal(articleId, shippedItem["articleId"]!.ToString());
        Assert.Equal("1", shippedItem["quantity"]!.ToString());

        output.WriteLine($"LIVE REAL AR: order {orderId} → shipment {shipment["id"]} created, " +
                         "item derived, data written, status SHIPPED persisted (read-back verified)");
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        // Best-effort cleanup so repeated runs do not litter the trial (a SHIPPED shipment
        // may refuse deletion — then it simply stays; the trial is a sandbox).
        var shipmentId = shipment["id"]!.ToString();
        var shipmentGone = (await api.SendAsync(HttpMethod.Delete, $"shipment/id/{shipmentId}", null)).IsSuccess;
        var orderGone = shipmentGone &&
                        (await api.SendAsync(HttpMethod.Delete, $"salesOrder/id/{orderId}", null)).IsSuccess;
        output.WriteLine($"  cleanup: shipment deleted={shipmentGone}, order deleted={orderGone}");
    }

    [Fact]
    public async Task BeWrite_RealWrite_BooksDeltaIdempotentlyAndRestoresStock()
    {
        if (RealWriteConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: real-write gate not open (WECLAPP_TRIAL_REAL_WRITE=1 + trial envs required).");
            return;
        }

        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);
        var warehouse = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("warehouse"), "GET warehouse"))!["result"]!.AsArray().OfType<JsonNode>()
            .FirstOrDefault(w => !string.IsNullOrEmpty(w["defaultStoragePlaceId"]?.ToString()));
        // Bookings are rejected for non-storable articles (trial-proven 400) — pick a STORABLE one.
        var articleId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("article?page=1&pageSize=50&properties=id,articleType"), "GET article"))!["result"]!
            .AsArray().OfType<JsonNode>()
            .FirstOrDefault(a => a["articleType"]?.ToString() == "STORABLE")?["id"]?.ToString();
        if (warehouse is null || string.IsNullOrEmpty(articleId))
        {
            output.WriteLine("SKIPPED: trial lacks a warehouse with default place or a STORABLE article.");
            return;
        }

        var warehouseId = warehouse["id"]!.ToString();

        async Task<decimal> CurrentStockAsync()
        {
            var rows = JsonNode.Parse(WeClappApi.EnsureSuccess(
                    await api.GetAsync($"warehouseStock?articleId-eq={articleId}&warehouseId-eq={warehouseId}"),
                    "GET warehouseStock"))!["result"]!.AsArray();
            return rows.OfType<JsonNode>()
                .Sum(r => decimal.Parse(r["quantity"]?.ToString() ?? "0", CultureInfo.InvariantCulture));
        }

        var initial = await CurrentStockAsync();
        var target = initial + 2;

        var config = new WeClappBeWriteNodeConfiguration
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            WarehouseId = warehouseId,
            DryRun = false, // REAL bookings — trial only, double-gated
        };

        async Task<List<string>> RunNodeAsync(decimal quantity, string label)
        {
            var content = $"{articleId}|0|0||{quantity.ToString(CultureInfo.InvariantCulture).Replace('.', ',')}|VER\r\n";
            var (dataContext, nodeContext, next, infos) = FakePipeline($"REAL-BE-{label}.txt", content);
            A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>()).Returns(config);
            var node = new WeClappBeWriteNode(next, A.Fake<ILogger<WeClappBeWriteNode>>(), LiveHttpClientFactory(),
                A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext>());
            await node.ProcessObjectAsync(dataContext, nodeContext);
            return infos;
        }

        // 1) Real incoming booking (+2) with read-back.
        await RunNodeAsync(target, "up");
        Assert.Equal(target, await CurrentStockAsync());

        // 2) Idempotent re-run of the SAME snapshot: delta 0, nothing booked.
        var rerunInfos = await RunNodeAsync(target, "rerun");
        Assert.Equal(target, await CurrentStockAsync());
        Assert.Contains(rerunInfos, m => m.Contains("0 movements") && m.Contains("1 in sync"));

        // 3) Restore the original quantity via a real outgoing booking (cleanup by design).
        await RunNodeAsync(initial, "down");
        Assert.Equal(initial, await CurrentStockAsync());

        output.WriteLine($"LIVE REAL BE: article {articleId} in warehouse {warehouseId} " +
                         $"{initial} → {target} → (idempotent) → {initial}, all read-back verified");
    }
}
