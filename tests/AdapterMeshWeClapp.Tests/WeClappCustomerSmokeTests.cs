using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Environment-gated LIVE smoke against a real WeClapp account (trial now, customer later):
/// runs only when WECLAPP_TRIAL_API_KEY and WECLAPP_TRIAL_BASEURL are set (process or user
/// scope) and is a no-op otherwise. Verifies the real API contract our fetch node relies on:
/// AuthenticationToken header, page/pageSize paging, the {result:[...]} envelope, and the
/// customer join via the id-eq filter. Logs counts only — never payload contents or the key.
/// </summary>
public class WeClappTrialSmokeTests(ITestOutputHelper output)
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

    private (WeClappFetchTriggerNode Node, ITriggerContext Context, List<JsonNode?> Documents)
        CreateLiveSut(string baseUrl, string apiKey, string entity)
    {
        var context = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => context.NodeContext).Returns(nodeContext);
        A.CallTo(() => nodeContext.GetNodeConfiguration<WeClappFetchTriggerNodeConfiguration>())
            .Returns(new WeClappFetchTriggerNodeConfiguration
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Entity = entity,
                PageSize = 10,
            });

        var documents = new List<JsonNode?>();
        A.CallTo(() => context.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes(call => documents.Add((JsonNode?)call.Arguments[1]))
            .Returns(Task.FromResult<object?>(null));

        // Same handler setup as the adapter's named client: WeClapp serves gzip.
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(
            new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }));

        var node = new WeClappFetchTriggerNode(A.Fake<ILogger<WeClappFetchTriggerNode>>(), httpClientFactory);
        return (node, context, documents);
    }

    [Fact]
    public async Task FetchOnce_LiveArticles_PullsAndBindsRealData()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_TRIAL_API_KEY / WECLAPP_TRIAL_BASEURL not set.");
            return;
        }

        var (node, context, documents) = CreateLiveSut(baseUrl, apiKey, "article");

        await node.FetchOnceAsync(context);

        output.WriteLine($"LIVE articles pulled: {documents.Count}");
        Assert.NotEmpty(documents);
        Assert.All(documents, d => Assert.False(
            string.IsNullOrEmpty(d?["item"]?["id"]?.ToString()),
            "every article document must carry a non-empty item.id"));
    }

    [Fact]
    public async Task FetchOnce_LiveOrders_JoinCustomerViaIdEqFilter()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_TRIAL_API_KEY / WECLAPP_TRIAL_BASEURL not set.");
            return;
        }

        var (node, context, documents) = CreateLiveSut(baseUrl, apiKey, "salesOrder");

        await node.FetchOnceAsync(context);

        output.WriteLine($"LIVE orders pulled: {documents.Count}, " +
                         $"with customer: {documents.Count(d => d?["customer"] is not null)}");
        Assert.NotEmpty(documents);
        // The id-eq customer join is the one query-syntax fact we could not verify offline:
        // every order that carries a customerId must have been joined successfully.
        Assert.All(documents, d =>
        {
            var customerId = d?["item"]?["customerId"]?.ToString() ?? "";
            if (customerId.Length > 0)
            {
                Assert.False(string.IsNullOrEmpty(d?["customer"]?["id"]?.ToString()),
                    $"order with customerId must carry the joined customer (id {customerId})");
            }
        });
    }
}
