using FakeItEasy;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class WeClappToCkNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly WeClappToCkNode _sut;

    public WeClappToCkNodeTests()
    {
        _sut = new WeClappToCkNode(_next);
    }

    private WeClappToCkNodeConfiguration Configure(string mode)
    {
        var config = new WeClappToCkNodeConfiguration
        {
            Mode = mode,
            Path = "$.item",
            TargetPath = "$.ck",
            CustomerPath = "$.customer",
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<WeClappToCkNodeConfiguration>()).Returns(config);
        return config;
    }

    [Fact]
    public async Task ProcessObjectAsync_ArticleMode_MapsCkAttributeNames()
    {
        var config = Configure("Article");
        CkArticle? written = null;
        A.CallTo(() => _dataContext.Set(config.TargetPath, A<CkArticle>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes(call => written = (CkArticle?)call.Arguments[1]);
        A.CallTo(() => _dataContext.Get<WeClappArticle>("$.item")).Returns(new WeClappArticle
        {
            Id = "43222003744925",
            Name = "Ersatzglas VOLT",
            ArticleNumber = "VOLT-EG",
            Ean = "4064796000000",
            ArticleType = "STORABLE",
        });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(written);
        // ArticleNumber carries the stable WeClapp id — the DILOS field contract key
        // (AS field 3 and AI P* field 5 use the same id via the writers).
        Assert.Equal("43222003744925", written.ArticleNumber);
        Assert.Equal("Ersatzglas VOLT", written.Name);
        Assert.Equal("4064796000000", written.Ean);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_ArticleMode_FiltersSystemArticleAndStopsPipeline()
    {
        Configure("Article");
        A.CallTo(() => _dataContext.Get<WeClappArticle>("$.item")).Returns(new WeClappArticle
        {
            Id = "1",
            Name = "Palette",
            ArticleType = "LOADING_EQUIPMENT",
        });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_OrderMode_MapsCustomerOrderAndFlattenedItems()
    {
        var config = Configure("Order");
        CkOrderDocument? written = null;
        A.CallTo(() => _dataContext.Set(config.TargetPath, A<CkOrderDocument>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes(call => written = (CkOrderDocument?)call.Arguments[1]);
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.item")).Returns(new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",
            CustomerNumber = "7067387625809",
            OrderDate = 1707177600000L, // 2024-02-06 UTC
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "2", NetAmount = "59.98", Title = "Ersatzglas VOLT"
                }
            },
        });
        A.CallTo(() => _dataContext.Get<WeClappCustomer>("$.customer")).Returns(new WeClappCustomer
        {
            CustomerNumber = "7067387625809",
            Company = "TJ Lucas GmbH",
            FirstName = "TJ",
            LastName = "Lucas",
            Email = "tj@example.com",
            Addresses =
            {
                new WeClappAddress
                {
                    Street1 = "Im Wielputzfeld 15a", Zipcode = "51503", City = "Rösrath", CountryCode = "DE"
                }
            },
        });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(written);
        Assert.Equal("7067387625809", written.Customer.CustomerNumber);
        Assert.Equal("TJ Lucas GmbH", written.Customer.Name);
        Assert.Equal("TJ Lucas GmbH", written.Customer.Contact.CompanyName);
        Assert.Equal("tj@example.com", written.Customer.Contact.Email);
        Assert.NotNull(written.Customer.Contact.Address);
        Assert.Equal("Im Wielputzfeld 15a", written.Customer.Contact.Address.Street);
        Assert.Equal("Rösrath", written.Customer.Contact.Address.CityTown);
        Assert.Equal("DE", written.Customer.Contact.Address.NationalCode);

        Assert.Equal("5910986621265", written.Order.OrderNumber);       // WeClapp id = AI Auftragsnummer1
        Assert.Equal("74299", written.Order.ExternalOrderNumber);       // shop number = AI Auftragsnummer2
        Assert.Equal(new DateTime(2024, 2, 6, 0, 0, 0, DateTimeKind.Utc), written.Order.OrderDate);

        var item = Assert.Single(written.OrderItems);
        Assert.Equal(1, item.PositionNumber);
        Assert.Equal(2d, item.Quantity);
        Assert.Equal(29.99d, item.UnitPriceNet);                        // 59.98 / 2
        Assert.Equal("43222003744925", item.ArticleNumber);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_OrderMode_FiltersAnonymousDebitorAndStopsPipeline()
    {
        Configure("Order");
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.item")).Returns(new WeClappSalesOrder
        {
            Id = "1",
            CustomerNumber = "ANONYMOUS_DEBITOR",
        });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_OrderMode_MissingCustomerThrows()
    {
        Configure("Order");
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.item")).Returns(new WeClappSalesOrder
        {
            Id = "1",
            CustomerNumber = "10000",
        });
        A.CallTo(() => _dataContext.Get<WeClappCustomer>("$.customer")).Returns(null);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_OrderMode_UnparsableQuantityThrows()
    {
        Configure("Order");
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.item")).Returns(new WeClappSalesOrder
        {
            Id = "1",
            CustomerNumber = "10000",
            OrderItems = { new WeClappOrderItem { PositionNumber = 1, ArticleId = "A", Quantity = "abc" } },
        });
        A.CallTo(() => _dataContext.Get<WeClappCustomer>("$.customer")).Returns(new WeClappCustomer
        {
            CustomerNumber = "10000",
        });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownMode_Throws()
    {
        Configure("XX");

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_CustomerNameFallsBackToPersonName()
    {
        var config = Configure("Order");
        CkOrderDocument? written = null;
        A.CallTo(() => _dataContext.Set(config.TargetPath, A<CkOrderDocument>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes(call => written = (CkOrderDocument?)call.Arguments[1]);
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.item")).Returns(new WeClappSalesOrder
        {
            Id = "1",
            CustomerNumber = "10000",
        });
        A.CallTo(() => _dataContext.Get<WeClappCustomer>("$.customer")).Returns(new WeClappCustomer
        {
            CustomerNumber = "10000",
            FirstName = "Max",
            LastName = "Muster",
        });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.NotNull(written);
        Assert.Equal("Max Muster", written.Customer.Name);
        Assert.Null(written.Customer.Contact.Address); // no address in source → none invented
    }
}
