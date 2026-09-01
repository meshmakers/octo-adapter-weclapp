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
    public void ParseArticles_ComputesPurchasePriceFromEnrichedSupplySources()
    {
        // Raw GET /article embeds only supply-source REFERENCE STUBS without prices
        // (customer-verified 2026-07-08); the fetch side replaces them with the full
        // articleSupplySource entities. This parses the ENRICHED shape.
        var arts = WeClappJson.ParseArticles(
            """
            {"result":[
              {"id":"1","supplySources":[{"articlePrices":[{"price":"12.34"}]}]},
              {"id":"2","supplySources":[{"articlePrices":[]}]},
              {"id":"3"}
            ]}
            """);

        Assert.Equal(12.34m, arts[0].PurchasePrice);
        Assert.Null(arts[1].PurchasePrice);
        Assert.Null(arts[2].PurchasePrice);
    }

    [Fact]
    public void ParseArticles_ReadsSupplySourceStubReferences()
    {
        // The raw stub shape: {articleSupplySourceId} pointing at the separate entity.
        var arts = WeClappJson.ParseArticles(
            """{"result":[{"id":"1","supplySources":[{"articleSupplySourceId":"S1"}]}]}""");

        Assert.Equal("S1", Assert.Single(Assert.Single(arts).SupplySources).ArticleSupplySourceId);
    }

    [Fact]
    public void ParseArticleSupplySources_ReadsIdAndPrices()
    {
        var sources = WeClappJson.ParseArticleSupplySources(
            """{"result":[{"id":"S1","articlePrices":[{"price":"12.34"}]}]}""");

        var s = Assert.Single(sources);
        Assert.Equal("S1", s.Id);
        Assert.Equal("12.34", Assert.Single(s.ArticlePrices).Price);
    }

    // The AI price fields read three properties WeClapp puts on every order position (95/95 on
    // the customer account, read-only census 2026-09-01) and that the model ignored until now:
    // the line gross beside the line net, and the tax entity the rate is resolved from. Pinned
    // against the captured response rather than a hand-written literal.
    [Fact]
    public void ParseOrders_ReadsThePositionAmountsAndTaxReference()
    {
        var orders = WeClappJson.ParseOrders(Fx("salesOrder.json"));

        var item = Assert.Single(orders.SelectMany(o => o.OrderItems));
        Assert.Equal("37.23", item.NetAmount);
        Assert.Equal("44.67", item.GrossAmount);
        Assert.Equal("1", item.Quantity);
        Assert.Equal("3681", item.TaxId);
    }

    // Shipping cost items carry the same three properties (union of the live shapes) - the
    // shipping pseudo line needs them for exactly the same fields.
    [Fact]
    public void ParseOrders_ReadsTheShippingCostAmountsAndTaxReference()
    {
        var orders = WeClappJson.ParseOrders(
            """
            {"result":[{"id":"1","shippingCostItems":[
              {"netAmount":"4.50","grossAmount":"5.40","taxId":"3681"}]}]}
            """);

        var shipping = Assert.Single(Assert.Single(orders).ShippingCostItems);
        Assert.Equal("4.50", shipping.NetAmount);
        Assert.Equal("5.40", shipping.GrossAmount);
        Assert.Equal("3681", shipping.TaxId);
    }

    // GET /tax states the rate as a STRING, and it is the only property besides the id the AI
    // export reads off that entity.
    [Fact]
    public void ParseTaxes_ReadsIdAndRate()
    {
        var taxes = WeClappJson.ParseTaxes(
            """{"result":[{"id":"3681","name":"AT Umsatzsteuer","taxValue":"20"}]}""");

        var tax = Assert.Single(taxes);
        Assert.Equal("3681", tax.Id);
        Assert.Equal("20", tax.TaxValue);
    }

    [Fact]
    public void ParseOrders_ReadsShipmentMethodId()
    {
        var orders = WeClappJson.ParseOrders(
            """{"result":[{"id":"1","shipmentMethodId":"3415"}]}""");

        Assert.Equal("3415", Assert.Single(orders).ShipmentMethodId);
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
