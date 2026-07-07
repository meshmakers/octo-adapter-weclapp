using System.Text;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// Minimal WeClapp REST helper shared by the write-back nodes: AuthenticationToken header,
/// transient retries (5xx/408/429/network, exponential backoff — the fetch node's discipline),
/// and the page/pageSize loop. Non-transient HTTP errors are returned as a
/// <see cref="Result"/> so callers can treat expected ones (e.g. 404 = dead-letter) as data,
/// not exceptions.
/// </summary>
internal sealed class WeClappApi(
    HttpClient http,
    string baseUrl,
    string apiKey,
    int maxRetries,
    double retryBackoffBaseSeconds)
{
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public sealed record Result(int StatusCode, string Body)
    {
        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    public Task<Result> GetAsync(string pathAndQuery) => SendAsync(HttpMethod.Get, pathAndQuery, null);

    public async Task<Result> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body)
    {
        var url = $"{_baseUrl}/{pathAndQuery.TrimStart('/')}";
        string? lastError = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Add("AuthenticationToken", apiKey);
                if (body is not null)
                {
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }

                using var response = await http.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                var status = (int)response.StatusCode;
                var transient = status >= 500 || status == 408 || status == 429;
                if (!transient)
                {
                    return new Result(status, responseBody);
                }

                lastError = $"HTTP {status}: {Truncate(responseBody, 300)}";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            if (attempt < maxRetries && retryBackoffBaseSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryBackoffBaseSeconds * Math.Pow(2, attempt - 1)));
            }
        }

        throw new WeClappPipelineExecutionException(
            $"WeClapp request failed after {maxRetries} attempts ({lastError}) for {url}");
    }

    /// <summary>Fetches all pages of <c>{entity}?page=N&amp;pageSize=M[&amp;query]</c> until a short page.</summary>
    public async Task<List<JsonNode>> GetPagedAsync(string entity, string query, int pageSize)
    {
        var results = new List<JsonNode>();
        var page = 1;

        while (true)
        {
            var pathAndQuery = $"{entity}?page={page}&pageSize={pageSize}"
                               + (query.Length > 0 ? "&" + query : "");
            var body = EnsureSuccess(await GetAsync(pathAndQuery), $"GET {entity} page {page}");
            var result = JsonNode.Parse(body)?["result"]?.AsArray()
                         ?? throw new WeClappPipelineExecutionException(
                             $"WeClapp response for '{entity}' page {page} has no 'result' array");

            if (result.Count == 0)
            {
                break;
            }

            results.AddRange(result.OfType<JsonNode>());

            if (result.Count < pageSize)
            {
                break; // short page — the next one is guaranteed empty
            }

            page++;
        }

        return results;
    }

    /// <summary>Returns the body of a successful result; fails loud (with the WeClapp error
    /// body, which is self-explanatory JSON) otherwise.</summary>
    public static string EnsureSuccess(Result result, string what)
    {
        if (!result.IsSuccess)
        {
            throw new WeClappPipelineExecutionException(
                $"{what} failed with HTTP {result.StatusCode}: {Truncate(result.Body, 300)}");
        }

        return result.Body;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
