using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Xunit.Abstractions;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// TRIPLE-GATED end-to-end smoke over the REAL return-path chain: a test AR file on the
/// real LKV SFTP server → DilosFileFetch trigger with the production SshNetSftpFileSystemFactory
/// → real pipeline data context → WeClappArWrite → real SHIPPED shipment on the WeClapp TRIAL
/// → remote file deleted (delete-after-success). Gates: WECLAPP_TRIAL_API_KEY/BASEURL +
/// WECLAPP_TRIAL_REAL_WRITE=1 + LKV_SFTP_CREDENTIALS_FILE (path to the Server:/Benutzer:/PW:
/// file) — all process-scoped for one deliberate run; a normal `dotnet test` is a no-op.
/// Risk minimisation on the partner server (access granted by LKV for exactly such tests):
/// the file name AR_TEST_MESHMAKERS.TXT is unmistakably a test, the trigger pattern
/// AR_TEST*TXT can never touch a real AR file, the content references only OUR trial order,
/// and a finally block guarantees the file does not remain on the server. Credentials are
/// parsed in memory and never logged.
/// </summary>
public class LkvSftpE2eSmokeTests(ITestOutputHelper output)
{
    private const string TestFileName = "AR_TEST_MESHMAKERS.TXT";

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);

    private static (string BaseUrl, string ApiKey, SftpConnectionSettings Sftp)? Gate()
    {
        if (Env("WECLAPP_TRIAL_REAL_WRITE") != "1")
        {
            return null;
        }

        var apiKey = Env("WECLAPP_TRIAL_API_KEY");
        var baseUrl = Env("WECLAPP_TRIAL_BASEURL");
        var credentialsFile = Env("LKV_SFTP_CREDENTIALS_FILE");
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl) ||
            string.IsNullOrEmpty(credentialsFile) || !File.Exists(credentialsFile))
        {
            return null;
        }

        var raw = File.ReadAllText(credentialsFile);
        var host = Regex.Match(raw, @"Server:\s*(\S+)").Groups[1].Value.Replace("sftp://", "");
        var user = Regex.Match(raw, @"Benutzer:\s*(\S+)").Groups[1].Value;
        var password = Regex.Match(raw, @"(?m)PW:\s*(.+)$").Groups[1].Value.Trim();
        var port = 22;
        var hostPort = Regex.Match(host, @"^(.+):(\d+)$");
        if (hostPort.Success)
        {
            host = hostPort.Groups[1].Value;
            port = int.Parse(hostPort.Groups[2].Value);
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return (baseUrl, apiKey, new SftpConnectionSettings
        {
            Host = host,
            Port = port,
            Username = user,
            Password = password,
        });
    }

    private static HttpClient LiveHttpClient() => new(
        new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });

    private static IHttpClientFactory LiveHttpClientFactory()
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).Returns(LiveHttpClient());
        return factory;
    }

    [Fact]
    public async Task ArTestFile_FlowsFromLkvSftpThroughRealChainToShippedTrialShipment()
    {
        if (Gate() is not var (baseUrl, apiKey, sftpSettings))
        {
            output.WriteLine("SKIPPED: gate not open (trial envs + WECLAPP_TRIAL_REAL_WRITE=1 + LKV_SFTP_CREDENTIALS_FILE).");
            return;
        }

        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);

        // --- Trial arrange (same sequence as WeClappTrialRealWriteSmokeTests): fresh
        // confirmed order with one stocked STORABLE article. ---
        var articleId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("article?page=1&pageSize=50&properties=id,articleType"), "GET article"))!
            ["result"]!.AsArray().OfType<JsonNode>()
            .FirstOrDefault(a => a["articleType"]?.ToString() == "STORABLE")?["id"]?.ToString();
        var customerId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("salesOrder?pageSize=1"), "GET salesOrders"))!["result"]!.AsArray()
            .FirstOrDefault()?["customerId"]?.ToString();
        if (string.IsNullOrEmpty(articleId) || string.IsNullOrEmpty(customerId))
        {
            output.WriteLine("SKIPPED: trial lacks a STORABLE article or a customer.");
            return;
        }

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
                    ["movementNote"] = "sftp-e2e smoke arrange",
                }), "arrange stock");
        }

        var createdOrder = JsonNode.Parse(WeClappApi.EnsureSuccess(
            await api.SendAsync(HttpMethod.Post, "salesOrder", new JsonObject
            {
                ["customerId"] = customerId,
                ["orderItems"] = new JsonArray(new JsonObject
                {
                    ["articleId"] = articleId,
                    ["quantity"] = "1",
                }),
            }), "POST salesOrder"));
        var orderId = (createdOrder?["result"] ?? createdOrder)?["id"]?.ToString()
                      ?? throw new InvalidOperationException("POST salesOrder returned no id");
        var orderFull = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync($"salesOrder/id/{orderId}"), "GET salesOrder")) as JsonObject
            ?? throw new InvalidOperationException("salesOrder id-GET returned no object");
        orderFull["status"] = "ORDER_CONFIRMATION_PRINTED";
        WeClappApi.EnsureSuccess(
            await api.SendAsync(HttpMethod.Put, $"salesOrder/id/{orderId}", orderFull), "confirm order");
        output.WriteLine($"  arranged: trial order {orderId} (article {articleId}) confirmed");

        var today = DateTime.UtcNow.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var arContent =
            $"K*|1|1|E2E||{orderId}|E2E-2|0|0|0|{today}|1|1|2,5\r\n" +
            $"C*|{orderId}|9|LKVE2E0001|Karton|Standard|2,5\r\n" +
            $"P*|{orderId}|1|{articleId}||||||0|1|0\r\n" +
            $"L*|{orderId}|1|{articleId}||||||1|LKVE2E0001\r\n";

        // --- Upload the unmistakable test file to the LKV server; from here on a finally
        // block guarantees the server is left clean no matter what fails. ---
        using var sftp = new SftpClient(sftpSettings.Host, sftpSettings.Port,
            sftpSettings.Username, sftpSettings.Password!);
        sftp.Connect();
        Assert.DoesNotContain(sftp.ListDirectory("/"),
            f => f.Name.StartsWith("AR_TEST", StringComparison.OrdinalIgnoreCase)); // clean start
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(arContent)))
        {
            sftp.UploadFile(stream, $"/{TestFileName}", true);
        }

        output.WriteLine($"  uploaded: /{TestFileName} ({arContent.Length} chars)");

        try
        {
            // --- The REAL chain: production SFTP factory, real trigger, real data context,
            // real write node against the trial. ---
            var arNodeContext = A.Fake<INodeContext>();
            A.CallTo(() => arNodeContext.GetNodeConfiguration<WeClappArWriteNodeConfiguration>())
                .Returns(new WeClappArWriteNodeConfiguration
                {
                    BaseUrl = baseUrl,
                    ApiKey = apiKey,
                    DryRun = false,
                });
            A.CallTo(() => arNodeContext.Error(A<string>._, A<object[]>._)).Invokes(call =>
                output.WriteLine($"  arWrite ERROR: {NodeLogCapture.Format(call)}"));
            var arWriteNode = new WeClappArWriteNode(A.Fake<NodeDelegate>(),
                A.Fake<ILogger<WeClappArWriteNode>>(), LiveHttpClientFactory(),
                A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext>());

            var triggerContext = A.Fake<ITriggerContext>();
            var triggerNodeContext = A.Fake<INodeContext>();
            var globalConfiguration = A.Fake<IGlobalConfiguration>();
            A.CallTo(() => triggerContext.NodeContext).Returns(triggerNodeContext);
            A.CallTo(() => triggerContext.GlobalConfiguration).Returns(globalConfiguration);
            A.CallTo(() => globalConfiguration.IsDefined("LkvSftp")).Returns(true);
            A.CallTo(() => globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp"))
                .Returns(sftpSettings);
            A.CallTo(() => triggerNodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>())
                .Returns(new DilosFileFetchTriggerNodeConfiguration
                {
                    ServerConfiguration = "LkvSftp",
                    RemoteDirectory = "/",
                    FilePattern = "AR_TEST*TXT", // can never match a real AR file
                    MinFileAgeSeconds = 0, // the file was uploaded seconds ago on purpose
                    PollingIntervalSeconds = 900,
                });
            A.CallTo(() => triggerNodeContext.Error(A<string>._, A<object[]>._)).Invokes(call =>
                output.WriteLine($"  trigger ERROR: {NodeLogCapture.Format(call)}"));

            var executed = new List<string>();
            A.CallTo(() => triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
                .ReturnsLazily(async call =>
                {
                    var document = (JsonNode)call.Arguments[1]!;
                    executed.Add(document["fileName"]!.ToString());
                    using var dataContext = new DataContextImpl(JsonDocument.Parse(document.ToJsonString()));
                    await arWriteNode.ProcessObjectAsync(dataContext, arNodeContext);
                    return (object?)null;
                });

            var fetchNode = new DilosFileFetchTriggerNode(
                A.Fake<ILogger<DilosFileFetchTriggerNode>>(), new SshNetSftpFileSystemFactory());
            await fetchNode.FetchOnceAsync(triggerContext);

            // --- Proof: executed once, trial shipment SHIPPED with the E2E tracking,
            // and the remote file is GONE (delete-after-success). ---
            Assert.Equal(new[] { TestFileName }, executed);

            var readBack = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync($"shipment?salesOrderId-eq={orderId}"), "read-back"))!["result"]!.AsArray();
            var shipment = Assert.Single(readBack.OfType<JsonNode>());
            Assert.Equal("SHIPPED", shipment["status"]!.ToString());
            Assert.Equal("LKVE2E0001", shipment["packageTrackingNumber"]!.ToString());

            Assert.DoesNotContain(sftp.ListDirectory("/"),
                f => f.Name.StartsWith("AR_TEST", StringComparison.OrdinalIgnoreCase));

            output.WriteLine($"LIVE SFTP-E2E: /{TestFileName} → trigger → parse → trial order {orderId} " +
                             $"shipment {shipment["id"]} SHIPPED → remote file deleted. Full chain proven.");
        }
        finally
        {
            // Leave the partner server exactly as found — even on failure.
            if (sftp.Exists($"/{TestFileName}"))
            {
                sftp.DeleteFile($"/{TestFileName}");
                output.WriteLine("  finally: test file removed from server after failure");
            }

            sftp.Disconnect();
        }
    }

    private const string BeTestFileName = "BE_TEST_MESHMAKERS.txt";

    [Fact]
    public async Task BeTestFile_FlowsFromLkvSftpThroughRealChainToStockBooking()
    {
        if (Gate() is not var (baseUrl, apiKey, sftpSettings))
        {
            output.WriteLine("SKIPPED: gate not open (trial envs + WECLAPP_TRIAL_REAL_WRITE=1 + LKV_SFTP_CREDENTIALS_FILE).");
            return;
        }

        var api = new WeClappApi(LiveHttpClient(), baseUrl, apiKey, 4, 1);

        // --- Trial arrange: STORABLE article + the warehouse the BE node will reconcile. ---
        var articleId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("article?page=1&pageSize=50&properties=id,articleType"), "GET article"))!
            ["result"]!.AsArray().OfType<JsonNode>()
            .FirstOrDefault(a => a["articleType"]?.ToString() == "STORABLE")?["id"]?.ToString();
        var warehouseId = JsonNode.Parse(WeClappApi.EnsureSuccess(
                await api.GetAsync("warehouse"), "GET warehouse"))!["result"]!.AsArray().OfType<JsonNode>()
            .FirstOrDefault(w => !string.IsNullOrEmpty(w["defaultStoragePlaceId"]?.ToString()))?["id"]?.ToString();
        if (string.IsNullOrEmpty(articleId) || string.IsNullOrEmpty(warehouseId))
        {
            output.WriteLine("SKIPPED: trial lacks a STORABLE article or a warehouse with default place.");
            return;
        }

        async Task<decimal> CurrentStockAsync()
        {
            var rows = JsonNode.Parse(WeClappApi.EnsureSuccess(
                    await api.GetAsync($"warehouseStock?articleId-eq={articleId}&warehouseId-eq={warehouseId}"),
                    "GET warehouseStock"))!["result"]!.AsArray();
            return rows.OfType<JsonNode>()
                .Sum(r => decimal.Parse(r["quantity"]?.ToString() ?? "0", CultureInfo.InvariantCulture));
        }

        var initial = await CurrentStockAsync();
        var target = initial + 1;
        var beContent =
            $"{articleId}|0|0||{target.ToString(CultureInfo.InvariantCulture).Replace('.', ',')}|VER\r\n";

        using var sftp = new SftpClient(sftpSettings.Host, sftpSettings.Port,
            sftpSettings.Username, sftpSettings.Password!);
        sftp.Connect();
        Assert.DoesNotContain(sftp.ListDirectory("/"),
            f => f.Name.StartsWith("BE_TEST", StringComparison.OrdinalIgnoreCase)); // clean start
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(beContent)))
        {
            sftp.UploadFile(stream, $"/{BeTestFileName}", true);
        }

        output.WriteLine($"  uploaded: /{BeTestFileName} (stock {initial} → target {target})");

        try
        {
            var beNodeContext = A.Fake<INodeContext>();
            A.CallTo(() => beNodeContext.GetNodeConfiguration<WeClappBeWriteNodeConfiguration>())
                .Returns(new WeClappBeWriteNodeConfiguration
                {
                    BaseUrl = baseUrl,
                    ApiKey = apiKey,
                    WarehouseId = warehouseId,
                    DryRun = false, // REAL booking — trial only, triple-gated
                });
            A.CallTo(() => beNodeContext.Error(A<string>._, A<object[]>._)).Invokes(call =>
                output.WriteLine($"  beWrite ERROR: {NodeLogCapture.Format(call)}"));
            var beWriteNode = new WeClappBeWriteNode(A.Fake<NodeDelegate>(),
                A.Fake<ILogger<WeClappBeWriteNode>>(), LiveHttpClientFactory(),
                A.Fake<Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext>());

            var triggerContext = A.Fake<ITriggerContext>();
            var triggerNodeContext = A.Fake<INodeContext>();
            var globalConfiguration = A.Fake<IGlobalConfiguration>();
            A.CallTo(() => triggerContext.NodeContext).Returns(triggerNodeContext);
            A.CallTo(() => triggerContext.GlobalConfiguration).Returns(globalConfiguration);
            A.CallTo(() => globalConfiguration.IsDefined("LkvSftp")).Returns(true);
            A.CallTo(() => globalConfiguration.GetValue<SftpConnectionSettings>("LkvSftp"))
                .Returns(sftpSettings);
            A.CallTo(() => triggerNodeContext.GetNodeConfiguration<DilosFileFetchTriggerNodeConfiguration>())
                .Returns(new DilosFileFetchTriggerNodeConfiguration
                {
                    ServerConfiguration = "LkvSftp",
                    RemoteDirectory = "/",
                    FilePattern = "BE_TEST*txt", // can never match a real BE file
                    MinFileAgeSeconds = 0,
                    PollingIntervalSeconds = 3600,
                });
            A.CallTo(() => triggerNodeContext.Error(A<string>._, A<object[]>._)).Invokes(call =>
                output.WriteLine($"  trigger ERROR: {NodeLogCapture.Format(call)}"));

            var executed = new List<string>();
            A.CallTo(() => triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
                .ReturnsLazily(async call =>
                {
                    var document = (JsonNode)call.Arguments[1]!;
                    executed.Add(document["fileName"]!.ToString());
                    using var dataContext = new DataContextImpl(JsonDocument.Parse(document.ToJsonString()));
                    await beWriteNode.ProcessObjectAsync(dataContext, beNodeContext);
                    return (object?)null;
                });

            var fetchNode = new DilosFileFetchTriggerNode(
                A.Fake<ILogger<DilosFileFetchTriggerNode>>(), new SshNetSftpFileSystemFactory());
            await fetchNode.FetchOnceAsync(triggerContext);

            // --- Proof: executed once, stock really booked, remote file gone. ---
            Assert.Equal(new[] { BeTestFileName }, executed);
            Assert.Equal(target, await CurrentStockAsync());
            Assert.DoesNotContain(sftp.ListDirectory("/"),
                f => f.Name.StartsWith("BE_TEST", StringComparison.OrdinalIgnoreCase));

            output.WriteLine($"LIVE SFTP-E2E (BE): /{BeTestFileName} → trigger → parse → stock " +
                             $"{initial} → {target} booked in warehouse {warehouseId} → remote file deleted.");
        }
        finally
        {
            // Restore the trial stock and leave the partner server exactly as found.
            if (await CurrentStockAsync() > initial)
            {
                await api.SendAsync(HttpMethod.Post, "warehouseStockMovement/bookOutgoingMovement",
                    new JsonObject
                    {
                        ["articleId"] = articleId,
                        ["quantity"] = "1",
                        ["movementNote"] = "sftp-e2e BE smoke cleanup",
                    });
                output.WriteLine($"  cleanup: stock restored to {initial}");
            }

            if (sftp.Exists($"/{BeTestFileName}"))
            {
                sftp.DeleteFile($"/{BeTestFileName}");
                output.WriteLine("  finally: BE test file removed from server after failure");
            }

            sftp.Disconnect();
        }
    }
}
