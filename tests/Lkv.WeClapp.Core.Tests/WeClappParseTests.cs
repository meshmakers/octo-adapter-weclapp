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
}
