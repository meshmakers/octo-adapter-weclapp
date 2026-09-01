using System.Text.Json;
using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core;

/// <summary>Parses WeClapp REST responses (the standard <c>{ "result": [ … ] }</c> envelope).</summary>
public static class WeClappJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<WeClappArticle> ParseArticles(string json) => Parse<WeClappArticle>(json);

    public static IReadOnlyList<WeClappSalesOrder> ParseOrders(string json) => Parse<WeClappSalesOrder>(json);

    public static IReadOnlyList<WeClappCustomer> ParseCustomers(string json) => Parse<WeClappCustomer>(json);

    public static IReadOnlyList<WeClappArticleSupplySource> ParseArticleSupplySources(string json) =>
        Parse<WeClappArticleSupplySource>(json);

    public static IReadOnlyList<WeClappTax> ParseTaxes(string json) => Parse<WeClappTax>(json);

    private static IReadOnlyList<T> Parse<T>(string json)
    {
        var wrapper = JsonSerializer.Deserialize<ResultWrapper<T>>(json, Options);
        return wrapper?.Result ?? new List<T>();
    }

    private sealed record ResultWrapper<T>
    {
        public List<T> Result { get; init; } = new();
    }
}
