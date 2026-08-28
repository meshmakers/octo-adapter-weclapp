using System.Net;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Environment-gated LIVE smoke against the real customer WeClapp account (successor of the
/// trial-account smokes; the trial account expired 2026-07): runs only when
/// WECLAPP_CUSTOMER_API_KEY and WECLAPP_CUSTOMER_BASEURL are set (process or user scope)
/// and is a no-op otherwise. STRICTLY read-only — the customer system is productive, GET
/// only; the former dry-run/real-write smokes died with the trial account and must not be
/// revived against this system. Verifies the real API contract the SHIPPED pipelines rely on,
/// request for request: the AuthenticationToken header, page/pageSize paging, the {result:[...]}
/// envelope the MakeHttpRequest@1 itemsPath addresses, the status-eq order filter and the id-eq
/// customer lookup. Logs counts only - never payload contents and never the key.
/// </summary>
public class WeClappCustomerSmokeTests(ITestOutputHelper output)
{
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);

    private static (string BaseUrl, string ApiKey)? LiveConfig()
    {
        var apiKey = Env("WECLAPP_CUSTOMER_API_KEY");
        var baseUrl = Env("WECLAPP_CUSTOMER_BASEURL");
        return string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl)
            ? null
            : (baseUrl, apiKey);
    }

    /// <summary>The client the shipped pipelines use: MakeHttpRequest@1 resolves the injected
    /// default HttpClient, which the adapter registers with automatic decompression because
    /// WeClapp answers gzip whether or not it was asked to.</summary>
    private static HttpClient CreateClient(string apiKey)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        });
        client.DefaultRequestHeaders.Add("AuthenticationToken", apiKey);
        return client;
    }

    /// <summary>Issues one GET and returns the parsed <c>result</c> array - the envelope every
    /// WeClapp entity response wraps its elements in, and the one the yamls name as itemsPath.</summary>
    private static async Task<JsonArray> GetResultAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET failed with HTTP {(int)response.StatusCode}");

        return JsonNode.Parse(body)?["result"]?.AsArray()
               ?? throw new InvalidOperationException("WeClapp response carries no 'result' array");
    }

    [Fact]
    public async Task LiveArticles_PagedRequestReturnsTheResultEnvelope()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_CUSTOMER_API_KEY / WECLAPP_CUSTOMER_BASEURL not set.");
            return;
        }

        using var client = CreateClient(apiKey);
        var articles = await GetResultAsync(client, $"{baseUrl.TrimEnd('/')}/article?page=1&pageSize=10");

        output.WriteLine($"LIVE articles pulled: {articles.Count}");
        Assert.NotEmpty(articles);
        Assert.All(articles, a => Assert.False(
            string.IsNullOrEmpty(a?["id"]?.ToString()),
            "every article must carry a non-empty id - the AS delivery keys Artikelnummer on it"));
    }

    // The as pipeline reads the purchase prices off a SEPARATE entity: raw articles embed
    // reference stubs only, so the delivery's EK-Preis column depends on articleSupplySource
    // being fetchable in its own right and on the stubs pointing at its ids.
    [Fact]
    public async Task LiveSupplySources_AreFetchableAsTheirOwnEntity()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_CUSTOMER_API_KEY / WECLAPP_CUSTOMER_BASEURL not set.");
            return;
        }

        using var client = CreateClient(apiKey);
        var sources = await GetResultAsync(client,
            $"{baseUrl.TrimEnd('/')}/articleSupplySource?page=1&pageSize=10");

        output.WriteLine($"LIVE articleSupplySource pulled: {sources.Count}");
        Assert.All(sources, s => Assert.False(
            string.IsNullOrEmpty(s?["id"]?.ToString()),
            "the resolution matches article stubs against this id"));
    }

    // The ai pipeline's order URL carries status-eq=ORDER_CONFIRMATION_PRINTED, and that filter is
    // the ONLY flood guard against mass-delivering the closed historical order stock. An IGNORED
    // filter looks identical to a working one at the HTTP level, so accepting the syntax is not
    // the fact worth smoking - narrowing is. The unfiltered page is pulled alongside it, and every
    // order in it that carries a different status must be ABSENT from the filtered result. That
    // assertion still means something when the account currently holds no confirmed order at all.
    [Fact]
    public async Task LiveOrders_StatusFilterIsAcceptedAndNarrowsTheResult()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_CUSTOMER_API_KEY / WECLAPP_CUSTOMER_BASEURL not set.");
            return;
        }

        using var client = CreateClient(apiKey);
        var root = baseUrl.TrimEnd('/');
        var confirmed = await GetResultAsync(client,
            $"{root}/salesOrder?status-eq=ORDER_CONFIRMATION_PRINTED&page=1&pageSize=10");
        var unfiltered = await GetResultAsync(client, $"{root}/salesOrder?page=1&pageSize=10");

        var confirmedIds = confirmed.Select(o => o?["id"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        var otherStatusIds = unfiltered
            .Where(o => o?["status"]?.ToString() != "ORDER_CONFIRMATION_PRINTED")
            .Select(o => o?["id"]?.ToString())
            .ToList();

        output.WriteLine($"LIVE confirmed orders pulled: {confirmed.Count}, " +
                         $"unfiltered page: {unfiltered.Count}, of those other status: {otherStatusIds.Count}");

        Assert.All(confirmed, o => Assert.Equal("ORDER_CONFIRMATION_PRINTED", o?["status"]?.ToString()));
        Assert.NotEmpty(otherStatusIds); // otherwise the account cannot demonstrate narrowing today
        Assert.All(otherStatusIds, id => Assert.DoesNotContain(id, confirmedIds));
    }

    // The id-eq customer join is the one query-syntax fact that cannot be verified offline. The
    // ai pipeline runs this lookup once per order and reads the record at result[0].
    [Fact]
    public async Task LiveCustomerLookup_JoinsTheOrdersCustomerViaIdEqFilter()
    {
        if (LiveConfig() is not var (baseUrl, apiKey))
        {
            output.WriteLine("SKIPPED: WECLAPP_CUSTOMER_API_KEY / WECLAPP_CUSTOMER_BASEURL not set.");
            return;
        }

        using var client = CreateClient(apiKey);
        var root = baseUrl.TrimEnd('/');
        var orders = await GetResultAsync(client, $"{root}/salesOrder?page=1&pageSize=10");
        Assert.NotEmpty(orders);

        // One lookup, on the first order that carries a customerId: the point is the query
        // syntax, and repeating it per order would only add load to a productive system.
        var customerId = orders
            .Select(o => o?["customerId"]?.ToString())
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        Assert.False(string.IsNullOrEmpty(customerId),
            "no order carries a customerId - the AI recipient join cannot be smoked");

        var customers = await GetResultAsync(client, $"{root}/customer?id-eq={customerId}");

        output.WriteLine($"LIVE orders pulled: {orders.Count}, customer lookup matched: {customers.Count}");
        var customer = Assert.Single(customers);
        Assert.Equal(customerId, customer?["id"]?.ToString());
    }
}
