using Lkv.WeClapp.Core.Model;

namespace Lkv.WeClapp.Core.Tests;

public class WeClappParseTests
{
    private static string Fx(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void ParseArticles_ReadsUnitNameAndType()
    {
        var arts = WeClappJson.ParseArticles(Fx("article.json"));

        Assert.Equal(2, arts.Count);
        Assert.Contains(arts, a => a.ArticleNumber == "000123" && a.UnitName == "kg" && a.ArticleType == "STORABLE");
        Assert.Contains(arts, a => a.ArticleType == "LOADING_EQUIPMENT"); // wird später gefiltert
    }

    [Fact]
    public void ParseOrders_ReadsCustomerAndItems()
    {
        var orders = WeClappJson.ParseOrders(Fx("salesOrder.json"));

        Assert.Equal(2, orders.Count);
        Assert.Contains(orders, o => o.CustomerNumber == "10000");
        Assert.All(orders, o => Assert.NotNull(o.OrderItems));
        Assert.Contains(orders, o => o.OrderItems.Count > 0);
    }

    [Fact]
    public void ParseCustomers_ReadsAnonymousDebitorSample()
    {
        var customers = WeClappJson.ParseCustomers(Fx("customer.json"));

        var anon = Assert.Single(customers, c => c.CustomerNumber == "ANONYMOUS_DEBITOR");
        Assert.Equal("ANONYMOUS_COMPANY", anon.Company);
        Assert.Equal("ORGANIZATION", anon.PartyType);
        Assert.NotNull(anon.Addresses); // trial sample: empty list, structure to re-verify with real data
    }
}
