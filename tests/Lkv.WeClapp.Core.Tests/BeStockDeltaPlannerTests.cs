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
        var lines = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "BE_*.txt")
            .Select(File.ReadAllText)
            .SelectMany(DilosBeParser.Parse)
            .ToList();
        Assert.True(lines.Count > 1000, $"expected >1000 golden BE lines, got {lines.Count}");
        Assert.All(lines, l => Assert.Equal(DilosStockStatus.Available, l.Status));

        var states = lines.Select(l => State(l, l.ArticleNumber)).ToList();
        var plan = BeStockDeltaPlanner.Plan(states, "be.txt");

        Assert.Empty(plan.Warnings);
        Assert.Equal(lines.Count(l => l.Quantity > 0), plan.Movements.Count);
        Assert.All(plan.Movements, m => Assert.Equal(StockMovementDirection.Incoming, m.Direction));
        Assert.Equal(lines.Count(l => l.Quantity == 0), plan.InSyncCount);
    }
}
