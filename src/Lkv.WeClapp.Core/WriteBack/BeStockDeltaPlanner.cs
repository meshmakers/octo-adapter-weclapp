using System.Globalization;
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.WriteBack;

/// <summary>
/// Pure planning logic for writing one DILOS BE stock snapshot back into WeClapp.
/// WeClapp's warehouseStock is read-only (API v1 and v2), so absolute BE quantities are
/// converted into delta movement bookings (POST /warehouseStockMovement/bookIncomingMovement
/// resp. bookOutgoingMovement). The caller resolves article ids and current stock rows,
/// then executes the returned movements. Only Available (VER) lines are booked; Blocked (GES)
/// lines — never seen in golden files — are skipped loudly until their semantics are agreed.
/// </summary>
public static class BeStockDeltaPlanner
{
    public static BeWritePlan Plan(IReadOnlyList<BeArticleState> articles, string beFileName)
    {
        var movements = new List<StockMovementPlan>();
        var warnings = new List<string>();
        var inSync = 0;
        var note = $"LKV BE {beFileName}";

        foreach (var article in articles)
        {
            var line = article.Line;
            if (line.Status == DilosStockStatus.Blocked)
            {
                warnings.Add($"Article {line.ArticleNumber}: GES (blocked) line skipped — blocked-stock handling is not agreed yet");
                continue;
            }

            if (article.ArticleId is null)
            {
                warnings.Add($"Article {line.ArticleNumber}: not found in WeClapp — line skipped");
                continue;
            }

            var current = article.CurrentRows.Sum(r => r.Quantity);
            var delta = line.Quantity - current;
            if (delta == 0)
            {
                inSync++;
                continue;
            }

            if (delta > 0)
            {
                movements.Add(new StockMovementPlan
                {
                    Direction = StockMovementDirection.Incoming,
                    ArticleId = article.ArticleId,
                    Quantity = Dec(delta),
                    StoragePlaceId = article.DefaultStoragePlaceId,
                    MovementNote = note
                });
                continue;
            }

            // delta < 0: deduct per storage-place row so every booking names its source.
            var remaining = -delta;
            foreach (var row in article.CurrentRows)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var take = Math.Min(row.Quantity, remaining);
                if (take <= 0)
                {
                    continue;
                }

                movements.Add(new StockMovementPlan
                {
                    Direction = StockMovementDirection.Outgoing,
                    ArticleId = article.ArticleId,
                    Quantity = Dec(take),
                    StoragePlaceId = row.StoragePlaceId,
                    MovementNote = note
                });
                remaining -= take;
            }

            if (remaining > 0)
            {
                warnings.Add($"Article {line.ArticleNumber}: could not allocate outgoing quantity {Dec(remaining)} to storage-place rows");
            }
        }

        return new BeWritePlan { Movements = movements, Warnings = warnings, InSyncCount = inSync };
    }

    private static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One BE line enriched with the WeClapp lookups the planner needs (resolved by the caller).</summary>
public sealed record BeArticleState
{
    public required DilosStockLine Line { get; init; }

    /// <summary>Resolved WeClapp article id, or null when the article does not exist.</summary>
    public string? ArticleId { get; init; }

    /// <summary>Current physical stock rows of this article in the LKV warehouse (per storage place).</summary>
    public IReadOnlyList<WeClappStockRow> CurrentRows { get; init; } = [];

    /// <summary>Storage place used for incoming bookings (the LKV warehouse's default place).</summary>
    public string? DefaultStoragePlaceId { get; init; }
}

/// <summary>One warehouseStock row (per storage place) as read from GET /warehouseStock.</summary>
public sealed record WeClappStockRow
{
    public required string StoragePlaceId { get; init; }

    public required decimal Quantity { get; init; }
}

public enum StockMovementDirection
{
    Incoming,
    Outgoing
}

/// <summary>One movement booking to execute (bookIncomingMovement / bookOutgoingMovement).</summary>
public sealed record StockMovementPlan
{
    public required StockMovementDirection Direction { get; init; }

    public required string ArticleId { get; init; }

    /// <summary>Positive quantity as dot-decimal string.</summary>
    public required string Quantity { get; init; }

    /// <summary>Target (incoming) resp. source (outgoing) storage place.</summary>
    public string? StoragePlaceId { get; init; }

    public required string MovementNote { get; init; }
}

/// <summary>Result of planning one BE file.</summary>
public sealed record BeWritePlan
{
    public IReadOnlyList<StockMovementPlan> Movements { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Lines whose WeClapp stock already matched the BE quantity.</summary>
    public int InSyncCount { get; init; }
}
