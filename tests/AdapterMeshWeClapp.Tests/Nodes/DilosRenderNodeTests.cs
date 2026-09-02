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

        // The tax entities the yaml fetches once per tick. Empty unless a test states otherwise:
        // a position without a taxId asks the set for nothing.
        Taxes();
    }

    private DilosRenderNodeConfiguration Configure(string mode, string submandant = "", string path = "$.items",
        string targetPath = "$.dilos", string fileNameTargetPath = "", string taxesPath = "$.taxes")
    {
        var config = new DilosRenderNodeConfiguration
        {
            Mode = mode,
            Submandant = submandant,
            Path = path,
            TargetPath = targetPath,
            FileNameTargetPath = fileNameTargetPath,
            TaxesPath = taxesPath,
        };
        A.CallTo(() => _nodeContext.GetNodeConfiguration<DilosRenderNodeConfiguration>()).Returns(config);
        return config;
    }

    private void Taxes(params WeClappTax[] taxes) =>
        A.CallTo(() => _dataContext.GetArray<WeClappTax>("$.taxes"))
            .Returns(taxes.Select(t => (WeClappTax?)t).ToList());

    // The context the node builds: RAW taxValue per id, exactly as the API states it.
    private static DilosOrderContext RenderContext(params (string Id, string? TaxValue)[] taxes) => new()
    {
        Submandant = "51696697501",
        TaxValueById = taxes.ToDictionary(t => t.Id, t => t.TaxValue, StringComparer.Ordinal),
    };

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
                    Quantity = "1", NetAmount = "29.99", GrossAmount = "35.99",
                    Title = "Ersatzglas VOLT"
                }
            },
            ShippingCostItems =
            {
                new WeClappShippingCostItem
                {
                    NetAmount = "4.50", GrossAmount = "5.40", Title = "DHL Standard (DE)",
                },
            }
        };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { order });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var ctx = RenderContext();
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
            GrossAmount = "35.99",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", GrossAmount = "35.99",
                    Title = "Ersatzglas VOLT"
                }
            },
        };
        A.CallTo(() => _dataContext.Get<WeClappSalesOrder>("$.items")).Returns(order);

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var ctx = RenderContext();
        var expected = DilosOrderWriter.RenderHeader(order, ctx) + "\n" +
                       string.Join("\n", DilosOrderWriter.RenderPositions(order, ctx)) + "\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    // --- the VAT rate the positions state --------------------------------------------------
    // WeClapp states a position's net and gross but no rate: the position names a tax ENTITY and
    // the rate lives there, so the AI render joins the fetched /tax set the way the AS delivery
    // joins the fetched articleSupplySource entities.

    [Fact]
    public async Task ProcessObjectAsync_AiMode_TakesThePositionRateFromTheFetchedTaxEntities()
    {
        var config = Configure("AI", submandant: "51696697501");
        Taxes(new WeClappTax { Id = "3681", TaxValue = "20" });
        var order = new WeClappSalesOrder
        {
            Id = "5910986621265",
            CustomerNumber = "7067387625809",
            GrossAmount = "44.67",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925", Quantity = "1",
                    NetAmount = "37.23", GrossAmount = "44.67", TaxId = "3681",
                },
            },
        };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { order });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        var ctx = RenderContext(("3681", "20"));
        var expected = DilosOrderWriter.RenderHeader(order, ctx) + "\n" +
                       string.Join("\n", DilosOrderWriter.RenderPositions(order, ctx)) + "\n";
        A.CallTo(() => _dataContext.Set(config.TargetPath, expected, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }

    // The rate is stated in whole percent and NOT as a DILOS tax key, because the partner's key
    // table maps 20 % to key 6 AND to key 20. A rate WeClapp carries with decimals still has to
    // reach the file as an integer - the field is declared "Zahl Integer ... ohne Kommastelle".
    [Theory]
    [InlineData("20", "20")]
    [InlineData("10", "10")]
    [InlineData("13.5", "14")]
    public async Task ProcessObjectAsync_AiMode_StatesTheRateAsWholePercent(string taxValue, string expected)
    {
        Configure("AI", submandant: "51696697501");
        Taxes(new WeClappTax { Id = "T", TaxValue = taxValue });
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { OrderTaxed("T") });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        Assert.Equal(expected, RenderedPositionField(16));
    }

    // The fetch is what makes the rate reachable at all, and a missing rate does NOT look wrong in
    // the delivered file: an empty VAT field is the legitimate value for a position that states no
    // tax, and the partner's own files carry it. A definition that lost the path would therefore
    // ship AI files without the promised rate, hourly, with nothing failing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessObjectAsync_BlankTaxesPath_FailsAsAConfigurationError(string? taxesPath)
    {
        Configure("AI", submandant: "51696697501", taxesPath: taxesPath!);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("'TaxesPath'", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // A path that names nothing is the same defect one step later: the join would find no rate for
    // any position and every AI file would go out with the field empty.
    [Fact]
    public async Task ProcessObjectAsync_NoTaxArrayAtTheTaxesPath_Throws()
    {
        Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetArray<WeClappTax>("$.taxes")).Returns(null);
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { OrderTaxed("T") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("$.taxes", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // The id is the only thing a position can point at, and the rate is the only thing the entity
    // is fetched for - an entity missing either is unusable, and both defects end in the same
    // indistinguishable place (a position whose rate silently stays empty).
    // The two halves fail in DIFFERENT places now, and the expected message says which: a
    // missing id makes the entity unreachable and is caught while the set is indexed, an
    // unreadable rate is caught at the position that names it. Asserting only the exception
    // type would let one guard silently cover for the other.
    [Theory]
    [InlineData(null, "20", "carries no 'id'")]
    [InlineData("", "20", "carries no 'id'")]
    [InlineData("3681", null, "is not a plain decimal percentage")]
    [InlineData("3681", "", "is not a plain decimal percentage")]
    [InlineData("3681", "not a number", "is not a plain decimal percentage")]
    public async Task ProcessObjectAsync_UnusableTaxEntity_Throws(string? id, string? taxValue,
        string expected)
    {
        Configure("AI", submandant: "51696697501");
        Taxes(new WeClappTax { Id = id!, TaxValue = taxValue });
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { OrderTaxed("3681") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_DuplicateTaxId_Throws()
    {
        Configure("AI", submandant: "51696697501");
        Taxes(new WeClappTax { Id = "3681", TaxValue = "20" },
            new WeClappTax { Id = "3681", TaxValue = "10" });
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { OrderTaxed("3681") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("appears more than once", ex.Message, StringComparison.Ordinal);

        // The same anchor the other refusals carry: a rejected tax set leaves nothing behind for
        // the delivery to pick up, and does not let the chain continue.
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // The writer refuses a position whose tax entity was not fetched. That refusal is raised
    // inside a per-order ForEach@1 carrying continueOnError, where an unattributed exception is
    // booked as one failed order with nothing pointing at the cause.
    [Fact]
    public async Task ProcessObjectAsync_PositionNamingAnUnfetchedTaxEntity_FailsAttributedToTheNode()
    {
        Configure("AI", submandant: "51696697501");
        Taxes(new WeClappTax { Id = "3681", TaxValue = "20" });
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { OrderTaxed("9999") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("DilosRender", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not among the", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    private static WeClappSalesOrder OrderTaxed(string taxId) => new()
    {
        Id = "5910986621265",
        CustomerNumber = "7067387625809",
        GrossAmount = "12.00",
        OrderItems =
        {
            new WeClappOrderItem
            {
                PositionNumber = 1, ArticleId = "43222003744925", Quantity = "1",
                NetAmount = "10.00", GrossAmount = "12.00", TaxId = taxId,
            },
        },
    };

    /// <summary>The single P* line of an <see cref="OrderTaxed"/> render, field by DILOS number,
    /// read back off the content write the node performed.</summary>
    private string RenderedPositionField(int dilosFieldNo)
    {
        var content = Fake.GetCalls(_dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Select(call => call.Arguments)
            .Where(arguments => (string?)arguments[0] == "$.dilos")
            .Select(arguments => arguments[1] as string)
            .Single();

        Assert.NotNull(content);
        return content.TrimEnd('\n').Split('\n')[1].Split('|')[dilosFieldNo - 1];
    }

    // An AI execution always renders at least its K* header, so no content means an upstream
    // defect. It must never continue: SftpUpload@1 would put an empty string on the LKV server
    // as a 0-byte file and the export marker behind it would then record a delivery that never
    // happened. (The AS side reaches the same end through the yaml's empty gate instead.)
    [Fact]
    public async Task ProcessObjectAsync_EmptyArray_ThrowsAndDoesNotContinue()
    {
        Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?>());

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("rendered no content", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // "AS" is now an unknown mode too: the article master renders through the product node, and
    // a yaml still asking this one for it must fail rather than deliver something else.
    [Theory]
    [InlineData("XX")]
    [InlineData("AS")]
    public async Task ProcessObjectAsync_UnknownMode_ThrowsAndDoesNotContinue(string mode)
    {
        Configure(mode);

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

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("Submandant", ex.Message, StringComparison.Ordinal);
    }

    // The properties are non-nullable, but a yaml carrying an explicit null ("submandant:" with no
    // value) assigns null OVER the initializer, so null is a real state a definition produces. Both
    // guards used to dereference it: measured as a bare NullReferenceException, raised INSIDE the
    // per-order ForEach@1 - which continueOnError then swallows as one failed order rather than
    // the configuration defect it is. Null now means what the empty string means at both sites.
    [Fact]
    public async Task ProcessObjectAsync_NullSubmandant_FailsAsAConfigurationError()
    {
        Configure("AI", submandant: null!);
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { MinimalOrder() });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("Submandant", ex.Message);
    }

    // The same class of defect as the null Submandant above, on the two paths this node reads and
    // writes. A null Path reached CanonicalPath as a raw NullReferenceException, raised inside the
    // per-order ForEach@1 - which continueOnError then books as one failed order instead of the
    // configuration defect it is, so the AI delivery stops with nothing pointing at the yaml.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessObjectAsync_BlankPath_FailsAsAConfigurationError(string? path)
    {
        Configure("AI", submandant: "51696697501", path: path!);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("'Path'", ex.Message);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // A blank TargetPath is the quieter half: the data context treats null and empty alike as "$",
    // so the rendered content REPLACES the loop document root instead of landing beside it. Nothing
    // fails - the delivery behind it simply finds nothing at the paths it reads.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessObjectAsync_BlankTargetPath_FailsAsAConfigurationError(string? targetPath)
    {
        Configure("AI", submandant: "51696697501", targetPath: targetPath!);
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { MinimalOrder() });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("'TargetPath'", ex.Message);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullFileNameTargetPath_RendersWithoutWritingAName()
    {
        Configure("AI", submandant: "51696697501", fileNameTargetPath: null!);
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { MinimalOrder() });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        // Exactly one string write - the content. No name is written, and above all nothing is
        // written to a null path.
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// An order that RENDERS - every amount the writer needs, stated. The file-name guards run
    /// after the content, so a fixture that cannot render never reaches them: the render refuses
    /// first, with a different message, and a test asserting only the exception TYPE would pass
    /// while proving nothing about the guard it is named for. Hence this helper, and hence the
    /// message assertions at those four tests.
    /// </summary>
    private static WeClappSalesOrder RenderableOrder(string id) => new()
    {
        Id = id,
        OrderNumber = "74299",
        CustomerNumber = "7067387625809",
        GrossAmount = "35.99",
        OrderItems =
        {
            new WeClappOrderItem
            {
                PositionNumber = 1, ArticleId = "43222003744925", Quantity = "1",
                NetAmount = "29.99", GrossAmount = "35.99",
            },
        },
    };

    private static WeClappSalesOrder MinimalOrder() => new()
    {
        Id = "5910986621265",
        CustomerNumber = "7067387625809",
        GrossAmount = "35.99",
        OrderItems =
        {
            new WeClappOrderItem
            {
                PositionNumber = 1, ArticleId = "43222003744925", Quantity = "1",
                NetAmount = "29.99", GrossAmount = "35.99",
            },
        },
    };

    [Fact]
    public async Task ProcessObjectAsync_MissingPath_Throws()
    {
        Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(null);

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("No order array found", ex.Message, StringComparison.Ordinal);
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
            GrossAmount = "35.99",
            OrderItems =
            {
                new WeClappOrderItem
                {
                    PositionNumber = 1, ArticleId = "43222003744925",
                    Quantity = "1", NetAmount = "29.99", GrossAmount = "35.99",
                    Title = "Ersatzglas VOLT"
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
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { RenderableOrder("1"), RenderableOrder("2") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("exactly one order per execution", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeFileNameWithEmptyId_Throws()
    {
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { RenderableOrder("") });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("no id (Auftragsnummer1)", ex.Message, StringComparison.Ordinal);
    }

    // fileNameTargetPath is optional: with it unset the node writes the content and nothing
    // else, rather than writing a name to some default path the yaml never mentions.
    [Fact]
    public async Task ProcessObjectAsync_WithoutFileNameTargetPath_WritesOnlyContent()
    {
        Configure("AI", submandant: "51696697501");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?>
            {
                new()
                {
                    Id = "5910986621265", CustomerNumber = "7067387625809", GrossAmount = "0.00",
                    OrderItems =
                    {
                        new WeClappOrderItem
                        {
                            PositionNumber = 1, ArticleId = "1", Quantity = "1",
                            NetAmount = "0.00", GrossAmount = "0.00",
                        },
                    },
                },
            });

        await _sut.ProcessObjectAsync(_dataContext, _nodeContext);

        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_AiModeFileNameWithNullId_ThrowsInsteadOfNre()
    {
        // STJ overwrites the "" default with null when the JSON carries "id": null —
        // the guard must answer with the domain exception, not a NullReferenceException.
        Configure("AI", submandant: "51696697501", fileNameTargetPath: "$.dilosAiFileName");
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { RenderableOrder(null!) });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("no id (Auftragsnummer1)", ex.Message, StringComparison.Ordinal);
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
            .Returns(new List<WeClappSalesOrder?> { RenderableOrder(id) });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("path separator or dot segment", ex.Message, StringComparison.Ordinal);
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

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("rendered no content", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    // A quantity that does not read as a number fails the order here rather than quietly inside the
    // arithmetic: fields 18 and 20 are DIVIDED by it, so the former 0 fallback made the record state
    // the LINE amount as the unit price - too high by exactly the quantity, with the line prices
    // beside it still correct and nothing downstream able to tell.
    [Fact]
    public async Task ProcessObjectAsync_PositionWithAnUnreadableQuantity_FailsAttributedToTheNode()
    {
        Configure("AI", submandant: "51696697501");
        var order = RenderableOrder("5910986621265");
        order.OrderItems[0] = order.OrderItems[0] with { Quantity = "drei" };
        A.CallTo(() => _dataContext.GetArray<WeClappSalesOrder>("$.items"))
            .Returns(new List<WeClappSalesOrder?> { order });

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => _sut.ProcessObjectAsync(_dataContext, _nodeContext));

        Assert.Contains("DilosRender", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Mengeabg", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }
}
