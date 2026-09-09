using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Extracts;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using static Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.PipelineYamlWalk;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Runs the per-file body of a shipped ar/be yaml with the REAL nodes on a real DataContextImpl,
/// against faked SFTP, WeClapp, CK cache and tenant repository. The one node left out is
/// GetOrCreateRtEntitiesByType@1: it is internal to the product, so its OUTPUT is seeded instead -
/// exactly the three values the node writes (a fresh rtId, the type id and the mod operation), on
/// the paths the yaml configures. Reading the pipeline definition instead of restating it is what
/// makes the callers tests of the shipped return path rather than tests of the nodes.
/// </summary>
internal sealed class ShippedReturnPathChain : IDisposable
{
    public const string TenantId = "test-tenant";
    public const string FileName = "AR00006946.TXT";
    public const string RemotePath = "/" + FileName;
    public const string LastWriteText = "2026-08-28T07:11:16.0670000Z";
    public const long Length = 430;

    public ISftpSession Session { get; } = A.Fake<ISftpSession>();
    public ITenantRepository TenantRepository { get; } = A.Fake<ITenantRepository>();
    public FakeHttpMessageHandler Http { get; }
    public DataContextImpl DataContext { get; }

    /// <summary>Every entity update the chain handed to the repository, in call order.</summary>
    public List<EntityUpdateInfo<RtEntity>> Applied { get; } = [];

    /// <summary>The marker the chain persisted, or null when ApplyChanges@2 was never reached.</summary>
    public EntityUpdateInfo<RtEntity>? Marker => Applied.SingleOrDefault();

    private readonly IServiceProvider _services;
    private readonly IfNodeConfiguration _slice;

    public static async Task<ShippedReturnPathChain> PrepareAsync(string yamlFileName, string mode, bool dryRun,
        UpdateKind probeResult, string content, Func<HttpRequestMessage, int, HttpResponseMessage>? responder = null)
    {
        var root = await PipelineDefinitions.DeserializeAsync(yamlFileName);
        var top = root.Transformations?.ToList() ?? [];
        var modeNode = Assert.Single(top.OfType<SetPrimitiveValueNodeConfiguration>(), n => n.TargetPath == "$.mode");
        var loop = Assert.Single(top.OfType<ForEachNodeConfiguration>());
        var loopBody = loop.Transformations?.ToList() ?? [];
        var probe = Assert.Single(loopBody.OfType<GetOrCreateRtEntitiesByTypeNodeConfiguration>());
        var deleteGate = Assert.IsType<IfNodeConfiguration>(loopBody[^1]);

        // The body as shipped, minus the probe, with the two operational switches set for the case.
        var body = new List<NodeConfiguration> { modeNode with { Value = mode } };
        body.AddRange(loopBody.Where(n => n is not GetOrCreateRtEntitiesByTypeNodeConfiguration));
        foreach (var write in Walk(body).OfType<WeClappWriteNodeConfiguration>())
        {
            write.DryRun = dryRun;
            write.RetryBackoffBaseSeconds = 0;   // the failure case retries; no waiting in a unit test
        }

        // A gate that is always open, so the body runs as ONE sequence on ONE context - which is
        // what the loop body sees for a single file.
        var slice = deleteGate with { Path = "$.run", Value = "yes", Transformations = body };

        // The document the loop body holds for one listed file, in the shape SftpList@1 emits.
        var dataContext = new DataContextImpl(JsonDocument.Parse($$$"""
            {"run":"yes",
             "current":{"name":"{{{FileName}}}","fullPath":"{{{RemotePath}}}","length":{{{Length}}},
                        "lastWriteTimeUtc":"{{{LastWriteText}}}",
                        "source":{"serverConfiguration":"LkvSftp","remoteDirectory":"/","filePattern":"AR*TXT"}
                       }
            }
            """));

        // What GetOrCreateRtEntitiesByType@1 writes (GetOrCreateRtEntitiesByTypeNode.cs:47-65 at
        // 586c18d), written the same way, on the paths the yaml configures.
        dataContext.Set(probe.RtIdTargetPath, OctoObjectId.GenerateNewId(), DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        dataContext.Set(probe.CkTypeIdTargetPath, "Industry.Logistics/InboundFile", DocumentModes.Extend,
            ValueKinds.Simple, TargetValueWriteModes.Overwrite);
        dataContext.Set(probe.ModOperationPath, probeResult, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        var http = new FakeHttpMessageHandler(responder ?? ((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)));
        return new ShippedReturnPathChain(slice, dataContext, http, content);
    }

    private ShippedReturnPathChain(IfNodeConfiguration slice, DataContextImpl dataContext, FakeHttpMessageHandler http,
        string content)
    {
        _slice = slice;
        DataContext = dataContext;
        Http = http;

        var etlContext = A.Fake<IMeshEtlContext>();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        var octoSession = A.Fake<IOctoSession>();
        var sessionFactory = A.Fake<ISftpSessionFactory>();
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        var ckCache = A.Fake<ICkCacheService>();

        A.CallTo(() => etlContext.TenantId).Returns(TenantId);
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => etlContext.TenantRepository).Returns(TenantRepository);
        A.CallTo(() => globalConfiguration.IsDefined("LkvSftp")).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<SftpServerSettings>("LkvSftp"))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://test.weclapp.com/webapp/api/v1", ApiKey = "test-key" });
        A.CallTo(() => sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
                A<INodeContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(Session));
        A.CallTo(() => Session.Download(RemotePath, A<long>._)).Returns(Encoding.Latin1.GetBytes(content));
        A.CallTo(() => Session.Delete(RemotePath)).Returns(true);
        A.CallTo(() => TenantRepository.GetSessionAsync()).Returns(Task.FromResult(octoSession));
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
                    IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) =>
                Applied.AddRange(updates.Cast<EntityUpdateInfo<RtEntity>>()));
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).Returns(new HttpClient(Http));
        SetupInboundFileType(ckCache);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataPipeline()            // the standard nodes of the body: SetPrimitiveValue, FormatString, If, DateTime
            .RegisterNode<SftpDownloadNode>()
            .RegisterNode<SftpDeleteNode>()
            .RegisterNode<CreateUpdateInfoNode>()
            .RegisterNode<ApplyChangesNode2>()
            .RegisterNode<WeClappArWriteNode>()
            .RegisterNode<WeClappBeWriteNode>();
        services.AddSingleton(etlContext);
        services.AddSingleton(sessionFactory);
        services.AddSingleton(httpClientFactory);
        services.AddSingleton(ckCache);
        _services = services.BuildServiceProvider();
    }

    public async Task RunAsync()
    {
        var rootContext = NodeContext.CreateRootNodeContext(_services, A.Fake<IPipelineLogger>(), DataContext);
        await new IfNode((_, _) => Task.CompletedTask)
            .ProcessObjectAsync(DataContext, rootContext.RegisterChildNode("If", 0, _slice, DataContext));
    }

    public void Dispose() => DataContext.Dispose();

    /// <summary>
    /// The CK type as CreateUpdateInfo@1 resolves it: the seven InboundFile attributes with the
    /// value types of Industry.Logistics 2.1.0 (attributes/inboundFile.yaml). Same shape the
    /// product's CreateUpdateInfoNodeTests build for one attribute.
    /// </summary>
    private static void SetupInboundFileType(ICkCacheService ckCache)
    {
        var attributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();
        foreach (var (name, type) in new[]
                 {
                     ("FileKey", AttributeValueTypesDto.String),
                     ("SourceName", AttributeValueTypesDto.String),
                     ("SourceDirectory", AttributeValueTypesDto.String),
                     ("FileName", AttributeValueTypesDto.String),
                     ("FileSize", AttributeValueTypesDto.Int64),
                     ("LastWriteUtc", AttributeValueTypesDto.DateTime),
                     ("ProcessedAt", AttributeValueTypesDto.DateTime),
                 })
        {
            var id = new CkId<CkAttributeId>("Industry.Logistics", new CkAttributeId(name));
            attributes[id] = new CkTypeAttributeGraph(
                ckAttributeId: id,
                attributeName: name,
                autoCompleteValues: null,
                valueType: type,
                valueCkRecordId: null,
                valueCkEnumId: null,
                autoIncrementReference: null,
                metaData: null,
                defaultValues: null,
                isOptional: true,
                description: null);
        }

        var graph = new CkTypeGraph(
            ckTypeId: new CkId<CkTypeId>("Industry.Logistics", new CkTypeId("InboundFile")),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: attributes,
            indexes: [],
            associations: new CkGraphDirectedAssociations([]),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false);

        CkTypeGraph? outGraph = graph;
        A.CallTo(() => ckCache.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out outGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(graph);
    }
}
