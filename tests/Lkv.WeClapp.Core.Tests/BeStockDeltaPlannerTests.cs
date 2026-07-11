using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.WriteBack;

namespace Lkv.WeClapp.Core.Tests;

public class BeStockDeltaPlannerTests
{
    private static DilosStockLine Ver(string articleNumber, decimal quantity) => new()
    {
        ArticleNumber = articleNumber,
        Characteristic1 = "0",
        Characteristic2 = "0",
        Quantity = quantity,
        Status = DilosStockStatus.Available
    };

    private static BeArticleState State(DilosStockLine line, string? articleId,
        params WeClappStockRow[] rows) => new()
        {
            Line = line,
            ArticleId = articleId,
            CurrentRows = rows,
            DefaultStoragePlaceId = "4244"
        };

    [Fact]
    public void Plan_HigherBeQuantity_BooksSingleIncomingDelta()
    {
        var plan = BeStockDeltaPlanner.Plan(
            [State(Ver("A1", 10m), "A1", new WeClappStockRow { StoragePlaceId = "P1", Quantity = 4m })],
            "BE_20240410153954163.txt");

        var m = Assert.Single(plan.Movements);
        Assert.Equal(StockMovementDirection.Incoming, m.Direction);
        Assert.Equal("6", m.Quantity);
        Assert.Equal("4244", m.StoragePlaceId);
        Assert.Equal("LKV BE BE_20240410153954163.txt", m.MovementNote);
    }

    [Fact]
    public void Plan_IncomingWithoutDefaultStoragePlace_SkipsLoudly()
    {
        // A booking without targetStoragePlaceId is rejected or lands unpredictably, and a
        // persistently rejected movement would wedge the file in retry — skip loudly; the
        // next BE heals the gap once the warehouse is configured.
        var state = State(Ver("A1", 10m), "A1",
            new WeClappStockRow { StoragePlaceId = "P1", Quantity = 4m }) with
        {
            DefaultStoragePlaceId = null
        };

        var plan = BeStockDeltaPlanner.Plan([state], "be.txt");

        Assert.Empty(plan.Movements);
        Assert.Contains(plan.Warnings, w => w.Contains("skipped") && w.Contains("no default storage place"));
    }

    [Fact]
    public void Plan_LowerBeQuantity_BooksOutgoingPerStoragePlaceRow()
    {
        var plan = BeStockDeltaPlanner.Plan(
            [State(Ver("A1", 2m), "A1",
                new WeClappStockRow { StoragePlaceId = "P1", Quantity = 5m },
                new WeClappStockRow { StoragePlaceId = "P2", Quantity = 3m })],
            "be.txt");

        Assert.Equal(2, plan.Movements.Count);
        Assert.All(plan.Movements, m => Assert.Equal(StockMovementDirection.Outgoing, m.Direction));
        Assert.Equal(("P1", "5"), (plan.Movements[0].StoragePlaceId, plan.Movements[0].Quantity));
        Assert.Equal(("P2", "1"), (plan.Movements[1].StoragePlaceId, plan.Movements[1].Quantity));
    }

    [Fact]
    public void Plan_TwoVariantLinesSameArticle_AggregateBeforePlanning()
    {
        // Two BE lines (characteristic variants) can resolve to the SAME WeClapp article.
        // Planned independently, each would book its full delta against the same current
        // stock (4 and 6 vs 10 → bogus -6 and -4). They must aggregate first.
        var rows = new[] { new WeClappStockRow { StoragePlaceId = "P1", Quantity = 10m } };
        var lineA = Ver("A1", 4m) with { Characteristic2 = "red" };
        var lineB = Ver("A1", 6m) with { Characteristic2 = "blue" };

        var plan = BeStockDeltaPlanner.Plan(
            [State(lineA, "77", rows), State(lineB, "77", rows)], "be.txt");

        Assert.Empty(plan.Movements);      // 4 + 6 == 10 → in sync
        Assert.Equal(1, plan.InSyncCount); // one ARTICLE, not two lines
    }

    [Fact]
    public void Plan_TwoVariantLinesSameArticleWithDelta_BooksOneAggregatedMovement()
    {
        var rows = new[] { new WeClappStockRow { StoragePlaceId = "P1", Quantity = 10m } };

        var plan = BeStockDeltaPlanner.Plan(
            [State(Ver("A1", 4m), "77", rows), State(Ver("A1", 8m), "77", rows)], "be.txt");

        var m = Assert.Single(plan.Movements);
        Assert.Equal(StockMovementDirection.Incoming, m.Direction);
        Assert.Equal("2", m.Quantity); // (4+8) - 10, once — not per line
    }

    [Fact]
    public void Plan_MatchingQuantity_NoMovementCountedAsInSync()
    {
        var plan = BeStockDeltaPlanner.Plan(
            [State(Ver("A1", 4m), "A1", new WeClappStockRow { StoragePlaceId = "P1", Quantity = 4m })],
            "be.txt");

        Assert.Empty(plan.Movements);
        Assert.Equal(1, plan.InSyncCount);
    }

    [Fact]
    public void Plan_ZeroBeQuantity_DrainsCurrentStock()
    {
        var plan = BeStockDeltaPlanner.Plan(
            [State(Ver("A1", 0m), "A1", new WeClappStockRow { StoragePlaceId = "P1", Quantity = 3m })],
            "be.txt");

        var m = Assert.Single(plan.Movements);
        Assert.Equal(StockMovementDirection.Outgoing, m.Direction);
        Assert.Equal("3", m.Quantity);
    }

    [Fact]
    public void Plan_BlockedLine_SkippedWithLoudWarning()
    {
        var blocked = Ver("A1", 5m) with { Status = DilosStockStatus.Blocked };

        var plan = BeStockDeltaPlanner.Plan([State(blocked, "A1")], "be.txt");

        Assert.Empty(plan.Movements);
        Assert.Contains(plan.Warnings, w => w.Contains("GES"));
    }

    [Fact]
    public void Plan_UnresolvedArticle_SkippedWithWarning()
    {
        var plan = BeStockDeltaPlanner.Plan([State(Ver("GHOST", 5m), null)], "be.txt");

        Assert.Empty(plan.Movements);
        Assert.Contains(plan.Warnings, w => w.Contains("GHOST"));
    }

    [Fact]
    public void Plan_AllGoldenBeLines_ProduceIncomingMovementsOnEmptyStock()
    {
        // One plan per FILE, like production (one trigger execution per BE file). Article
        // numbers are unique WITHIN each golden file (verified: 528/528, 528/528, 58/58) —
        // across the three daily snapshots they repeat, which the M3 aggregation would
        // (correctly) merge if the files were planned together.
        var totalLines = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "BE_*.txt"))
        {
            var lines = DilosBeParser.Parse(File.ReadAllText(file));
            totalLines += lines.Count;
            Assert.All(lines, l => Assert.Equal(DilosStockStatus.Available, l.Status));

            var states = lines.Select(l => State(l, l.ArticleNumber)).ToList();
            var plan = BeStockDeltaPlanner.Plan(states, Path.GetFileName(file));

            Assert.Empty(plan.Warnings);
            Assert.Equal(lines.Count(l => l.Quantity > 0), plan.Movements.Count);
            Assert.All(plan.Movements, m => Assert.Equal(StockMovementDirection.Incoming, m.Direction));
            Assert.Equal(lines.Count(l => l.Quantity == 0), plan.InSyncCount);
        }

        Assert.True(totalLines > 1000, $"expected >1000 golden BE lines, got {totalLines}");
    }
}
