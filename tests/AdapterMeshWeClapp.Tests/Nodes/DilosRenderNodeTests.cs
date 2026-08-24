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
        string targetPath = "$.dilos", string fileNameTargetPath = "")
    {
        var config = new DilosRenderNodeConfiguration
        {
            Mode = mode,
            Submandant = submandant,
            Path = path,
            TargetPath = targetPath,
            FileNameTargetPath = fileNameTargetPath,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>()).Returns(config);
        return config;
    }

    [Fact]
    public async Task ProcessObjectAsync_AsMode_RendersArticleLinesLfTerminated()
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
        // Line ending = LF: all real Billbee-produced AS/AI files are pure LF (CR count 0) —
        // the DILOS-import-proven format; CRLF only exists in files DILOS itself produces.
        var expected =
            DilosArticleWriter.RenderLine(articles[0]!, DilosArticleContext.Default) + "\n" +
            DilosArticleWriter.RenderLine(articles[1]!, DilosArticleContext.Default) + "\n";
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
        var expected = DilosOrderWriter.RenderHeader(order, ctx) + "\n" +
                       string.Join("\n", DilosOrderWriter.RenderPositions(order, ctx)) + "\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    // Per-document pipelines (one order per execution — golden AI files are one file per
    // order!) carry a single OBJECT at Path, not an array. The node must render it as one.
    [Fact]
    public async Task ProcessObjectAsync_AiMode_SingleObjectAtPathRendersOneOrder()
    {
        var config = Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetKind("$.items")).Returns(DataKind.Object);
        var order = new WeClappSalesOrder
        {
            Id = "5910986621265",
            CustomerNumber = "7067387625809",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", Title = "Ersatzglas VOLT"
                }
            },
        };
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.items")).Returns(order);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var ctx = new DilosOrderContext { Submandant = "51696697501" };
        var expected = DilosOrderWriter.RenderHeader(order, ctx) + "\n" +
                       string.Join("\n", DilosOrderWriter.RenderPositions(order, ctx)) + "\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    // Defensive twin of the only-system-articles case below: the Batch trigger never emits
    // an empty poll, but if an empty array ever reaches the render, emitting an empty AS
    // file would be a false snapshot — and nothing downstream would stop it, the delivery
    // node uploads empty content as a 0-byte file — so the render ends the pipeline.
    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_EndsPipelineWithoutOutput()
    {
        Configure("AS");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(new List<WeClappArticle?>());

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
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

    // --- fileNameTargetPath: golden DILOS file naming ------------------------------------
    // Billbee ground truth: SyncOrdersCommand builds "AI" + Auftragsnummer1 + ".txt",
    // SyncProductsCommand builds "AS" + local timestamp + ".txt". Golden samples:
    // AI5910748889425.txt, AS20240206020204.txt (14-digit yyyyMMddHHmmss).
    // Auftragsnummer1 = the WeClapp id (K* field 29, see the chain test) — NOT the shop
    // orderNumber (that is Auftragsnummer2): file name and K* line must carry the SAME
    // number. The AS timestamp is Vienna-local (DILOS operates Austrian local time; UTC
    // would shift late-evening polls to the previous day).

    [Fact]
    public async Task ProcessObjectAsync_AiModeWithFileNameTargetPath_NamesFileByAuftragsnummer1()
    {
        var config = Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        var order = new WeClappSalesOrder
        {
            Id = "5910986621265",
            OrderNumber = "74299",   // Auftragsnummer2 (shop number) — must NOT name the file
            CustomerNumber = "7067387625809",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", Title = "Ersatzglas VOLT"
                }
            },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { order });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set("$.dilosAiFileName", "AI5910986621265.txt", config.DocumentMode,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeFileNameWithMultipleOrders_ThrowsAmbiguousName()
    {
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        var orders = new List<WeClappSalesOrder?>
        {
            new() { Id = "1", OrderNumber = "622075", CustomerNumber = "1" },
            new() { Id = "2", OrderNumber = "622076", CustomerNumber = "1" },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items")).Returns(orders);

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeFileNameWithEmptyId_Throws()
    {
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { new() { Id = "", OrderNumber = "74299", CustomerNumber = "1" } });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_AsModeWithFileNameTargetPath_WritesViennaTimestampName()
    {
        // 2026-02-05 13:31:34 UTC = 14:31:34 Vienna (CET, UTC+1).
        var sut = new DilosRenderNode(_next, new FixedTimeProvider(
            new DateTimeOffset(2026, 2, 5, 13, 31, 34, TimeSpan.Zero)));
        var config = Configure("AS", fileNameTargetPath: "$.dilosAsFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(new List<WeClappArticle?> { new() { Id = "1", Name = "A", ArticleNumber = "A-1" } });

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set("$.dilosAsFileName", "AS20260205143134.txt", config.DocumentMode,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_AsModeFileName_LateEveningUtcRollsToNextViennaDay()
    {
        // 2026-07-11 22:30:00 UTC = 2026-07-12 00:30:00 Vienna (CEST, UTC+2) — the name
        // must carry the NEXT Vienna calendar day, not the UTC day.
        var sut = new DilosRenderNode(_next, new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 11, 22, 30, 0, TimeSpan.Zero)));
        Configure("AS", fileNameTargetPath: "$.dilosAsFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(new List<WeClappArticle?> { new() { Id = "1", Name = "A", ArticleNumber = "A-1" } });

        await sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set("$.dilosAsFileName", "AS20260712003000.txt", A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithoutFileNameTargetPath_WritesOnlyContent()
    {
        Configure("AS");
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items"))
            .Returns(new List<WeClappArticle?> { new() { Id = "1", Name = "A", ArticleNumber = "A-1" } });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
    }

    // The dedicated AS delivery pipeline has no WeClappToCk stage (which used to end the
    // pipeline for system articles) — the AS render must exclude them itself, or loading
    // equipment (pallets) leaks into the DILOS article master file.
    [Fact]
    public async Task ProcessObjectAsync_AsMode_ExcludesSystemArticles()
    {
        var config = Configure("AS");
        var articles = new List<WeClappArticle?>
        {
            new() { Id = "1", Name = "Ersatzglas VOLT", ArticleNumber = "VOLT-EG" },
            new() { Id = "2", Name = "Europalette", ArticleNumber = "PAL-1", ArticleType = "LOADING_EQUIPMENT" },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items")).Returns(articles);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var expected = DilosArticleWriter.RenderLine(articles[0]!, DilosArticleContext.Default) + "\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    // A batch can consist entirely of loading equipment (e.g. tenant bootstrap before
    // regular articles exist). Emitting empty content would deliver a 0-byte AS file to LKV
    // as a false snapshot — the render must end the pipeline instead.
    [Fact]
    public async Task ProcessObjectAsync_AsMode_BatchWithOnlySystemArticles_EndsPipelineWithoutOutput()
    {
        Configure("AS", fileNameTargetPath: "$.dilosAsFileName");
        var articles = new List<WeClappArticle?>
        {
            new() { Id = "1", Name = "Europalette", ArticleNumber = "PAL-1", ArticleType = "LOADING_EQUIPMENT" },
            new() { Id = "2", Name = "Gitterbox", ArticleNumber = "GIT-1", ArticleType = "LOADING_EQUIPMENT" },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappArticle>("$.items")).Returns(articles);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeFileNameWithNullId_ThrowsInsteadOfNre()
    {
        // STJ overwrites the "" default with null when the JSON carries "id": null —
        // the guard must answer with the domain exception, not a NullReferenceException.
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { new() { Id = null!, OrderNumber = "74299", CustomerNumber = "1" } });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));
    }

    // The AI name embeds the external WeClapp order number. A name carrying path segments
    // does not fail the delivery: it is resolved to its last segment and uploaded under
    // that name, so the file lands somewhere nobody looks and nothing reports it. The
    // render is where such a value has to die.
    [Theory]
    [InlineData("../5910986621265")]
    [InlineData("/etc/5910986621265")]
    [InlineData("59109\\86621265")]
    public async Task ProcessObjectAsync_AiModeFileNameWithPathCharacters_ThrowsAndDoesNotContinue(string id)
    {
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?>
            {
                new() { Id = id, OrderNumber = "74299", CustomerNumber = "7067387625809" },
            });

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // Empty AI content is the mirror of the empty-AS case, and it needs the opposite
    // answer: an execution reaching the render always carries exactly one order, which
    // always renders at least its K* header. Nothing downstream refuses "" — the delivery
    // node would upload a 0-byte file — so the render fails loudly and the tick retries.
    [Fact]
    public async Task ProcessObjectAsync_AiMode_EmptyContent_ThrowsAndDoesNotContinue()
    {
        Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?>());

        await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }
}
