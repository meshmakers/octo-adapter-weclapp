using System.Net;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// The shipped ar yaml, run for ONE listed file with the real nodes against fakes: the marker is
/// persisted BEFORE the file is deleted, only the live mode deletes, a known file is neither read
/// nor written again, and every failure before the marker leaves the file on the server. be shares
/// the structure (pinned by the yaml contracts); its write node has its own tests.
/// </summary>
public class ArBeChainTests
{
    // Golden AR00006946.TXT verbatim (order 400000001247987). The default responder answers the
    // order lookup with 404, so the write node dead-letters the shipment and makes no further
    // request - a consumed file without any WeClapp write, the shape of the staging proof files.
    private const string GoldenAr =
        "K*|1|1|400000001572890||400000001247987|TEST-123|1001801714|1400137|2|10.04.2024|1|1|2,5\r\n" +
        "C*|400000001247987|9|1013408501850970172035|Karton|Standard|2,5\r\n" +
        "P*|400000001247987|1|400000001273682||||||0|1|-1\r\n" +
        "L*|400000001247987|1|400000001273682||||||1|1013408501850970172035\r\n";

    private const string Yaml = "dilos-ar-to-weclapp.yaml";
    private const string LiveKey = "LkvSftp|/|AR00006946.TXT|430|2026-08-28T07:11:16.0670000Z|live";

    [Fact]
    public async Task NewFile_LiveMode_PersistsTheMarkerBeforeTheDelete()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "live", dryRun: false, UpdateKind.Insert, GoldenAr);

        await chain.RunAsync();

        // The key is the listing's own text plus the mode - nothing re-formatted.
        Assert.Equal(LiveKey, chain.DataContext.Get<string>("$.fileKey"));

        // Marker first, delete second: a crash between the two leaves a marked file, never a
        // deleted unmarked one.
        A.CallTo(() => chain.TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => chain.Session.Delete(ShippedReturnPathChain.RemotePath)).MustHaveHappenedOnceExactly());

        var marker = Assert.IsType<EntityUpdateInfo<RtEntity>>(chain.Marker);
        Assert.Equal(EntityModOptions.Insert, marker.ModOption);
        var attributes = marker.RtEntity!.Attributes;
        Assert.Equal(LiveKey, attributes["FileKey"]);
        Assert.Equal("LkvSftp", attributes["SourceName"]);
        Assert.Equal("/", attributes["SourceDirectory"]);
        Assert.Equal(ShippedReturnPathChain.FileName, attributes["FileName"]);
        // The SDK boxes the JSON number by its own rules (JsonScalar); the CK attribute type Int64 is what the tenant enforces.
        Assert.Equal(430L, Convert.ToInt64(attributes["FileSize"]));
        Assert.Equal(new DateTime(2026, 8, 28, 7, 11, 16, 67, DateTimeKind.Utc),
            Assert.IsType<DateTime>(attributes["LastWriteUtc"]));
        Assert.InRange(Assert.IsType<DateTime>(attributes["ProcessedAt"]),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task NewFile_DryRunMode_PersistsADryRunMarkerAndDeletesNothing()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "dryRun", dryRun: true, UpdateKind.Insert, GoldenAr);

        await chain.RunAsync();

        var marker = Assert.IsType<EntityUpdateInfo<RtEntity>>(chain.Marker);
        Assert.EndsWith("|dryRun", Assert.IsType<string>(marker.RtEntity!.Attributes["FileKey"]));
        A.CallTo(() => chain.Session.Delete(A<string>._)).MustNotHaveHappened();
        // The dry-run write still resolves the order (a GET) and nothing else.
        Assert.All(chain.Http.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task KnownFile_LiveMode_OnlyDeletes()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "live", dryRun: false, UpdateKind.Update, GoldenAr);

        await chain.RunAsync();

        A.CallTo(() => chain.Session.Download(A<string>._, A<long>._)).MustNotHaveHappened();
        Assert.Empty(chain.Http.Requests);
        Assert.Null(chain.Marker);
        A.CallTo(() => chain.Session.Delete(ShippedReturnPathChain.RemotePath)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task KnownFile_DryRunMode_DoesNothing()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "dryRun", dryRun: true, UpdateKind.Update, GoldenAr);

        await chain.RunAsync();

        A.CallTo(() => chain.Session.Download(A<string>._, A<long>._)).MustNotHaveHappened();
        Assert.Empty(chain.Http.Requests);
        Assert.Null(chain.Marker);
        A.CallTo(() => chain.Session.Delete(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RepositoryFailure_LeavesTheFileOnTheServer()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "live", dryRun: false, UpdateKind.Insert, GoldenAr);
        A.CallTo(() => chain.TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Throws(new InvalidOperationException("repository down"));

        var error = await Assert.ThrowsAnyAsync<Exception>(chain.RunAsync);

        Assert.Contains("repository down", Causes(error));
        A.CallTo(() => chain.Session.Delete(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WriteFailure_LeavesNoMarkerAndTheFileOnTheServer()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "live", dryRun: false, UpdateKind.Insert, GoldenAr,
            (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAnyAsync<Exception>(chain.RunAsync);

        Assert.Null(chain.Marker);
        A.CallTo(() => chain.Session.Delete(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FileAlreadyGone_LiveMode_IsNotAFailure()
    {
        using var chain = await ShippedReturnPathChain.PrepareAsync(Yaml, "live", dryRun: false, UpdateKind.Insert, GoldenAr);
        A.CallTo(() => chain.Session.Delete(ShippedReturnPathChain.RemotePath)).Returns(false);

        await chain.RunAsync();   // onMissingFile: Ignore - the goal state is reached either way

        Assert.NotNull(chain.Marker);
    }

    private static string Causes(Exception error)
    {
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" <- ", messages);
    }
}
