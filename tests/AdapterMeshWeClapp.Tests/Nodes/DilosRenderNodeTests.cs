using FakeItEasy;
using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

public class DilosRenderNodeTests
{
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext = A.Fake<INodeContext>();
    private readonly NodeDelegate _next = A.Fake<NodeDelegate>();
    private readonly DilosRenderNode _sut;

    public DilosRenderNodeTests()
    {
        _sut = new DilosRenderNode(_next);
    }

    private DilosRenderNodeConfiguration Configure(string mode, string submandant = "", string path = "$.items",
        string targetPath = "$.dilos")
    {
        var config = new DilosRenderNodeConfiguration
        {
            Mode = mode,
            Submandant = submandant,
            Path = path,
            TargetPath = targetPath,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>()).Returns(config);
        return config;
    }

    [Fact]
    public async Task ProcessObjectAsync_AsMode_RendersArticleLinesCrlfTerminated()
    {
        var config = Configure("AS");
        var articles = new List<WeClappArticle?>
        {
            new() { Id = "43222003744925", Name = "Ersatzglas VOLT", ArticleNumber = "VOLT-EG", UnitName = "pc." },
            new() { Id = "43222003744999", Name = "Brille NOVA", ArticleNumber = "NOVA-01", UnitName = "pc." },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items")).Returns(articles);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // The node orchestrates; line content is the writer's contract (already golden-tested).
        var expected =
            DilosArticleWriter.RenderLine(articles[0]!, DilosArticleContext.Default) + "\r\n" +
            DilosArticleWriter.RenderLine(articles[1]!, DilosArticleContext.Default) + "\r\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiMode_RendersHeaderAndPositionsPerOrder()
    {
        var config = Configure("AI", submandant: "51696697501");
        var order = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",
            CustomerNumber = "7067387625809",
            GrossAmount = "104.97",
            OrderDate = 1707177600000L,
            DeliveryAddress = new WeClappAddress { Company = "TJ Lucas", CountryCode = "DE" },
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", Title = "Ersatzglas VOLT"
                }
            },
            ShippingCostItems = { new WeClappShippingCostItem { NetAmount = "4.50", Title = "DHL Standard (DE)" } }
        };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { order });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var ctx = new DilosOrderContext { Submandant = "51696697501" };
        var expected = DilosOrderWriter.RenderHeader(order, ctx) + "\r\n" +
                       string.Join("\r\n", DilosOrderWriter.RenderPositions(order, ctx)) + "\r\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_WritesEmptyContent()
    {
        var config = Configure("AS");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(new List<WeClappArticle?>());

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set(config.TargetPath, "", config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownMode_ThrowsAndDoesNotContinue()
    {
        Configure("XX");

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeWithoutSubmandant_Throws()
    {
        Configure("AI", submandant: "");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?>());

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingPath_Throws()
    {
        Configure("AS");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(null);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }
}
