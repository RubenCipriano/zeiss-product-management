using ProductManagement.Core.Common;

namespace ProductManagement.Core.Products;

/// <summary>
/// A planned stock change ("add 5", "remove 3") in the form the database needs to apply it safely: the change
/// (<see cref="Delta"/>), the range the current stock must be in for it to be allowed (removing 3 needs at least 3),
/// and the ledger entry to write.
/// </summary>
/// <remarks>
/// The rule is a range, not a check against a loaded product, so the database can check and change the stock in one
/// conditional <c>UPDATE ... WHERE stock BETWEEN min AND max</c>. Check and write then happen as one step, which keeps
/// stock correct when many requests change the same product at once. See docs/adr/0003-atomic-stock-updates.md.
/// </remarks>
public class StockAdjustment
{
    private StockAdjustment(
        int delta,
        int minCurrentStock,
        int maxCurrentStock,
        string performedBy,
        string? requestKey,
        DateTimeOffset occurredAt)
    {
        Delta = delta;
        Reason = delta < 0 ? StockMovementReason.StockRemoved : StockMovementReason.StockAdded;
        MinCurrentStock = minCurrentStock;
        MaxCurrentStock = maxCurrentStock;
        PerformedBy = performedBy;
        RequestKey = requestKey;
        OccurredAt = occurredAt;
    }

    public int Delta { get; }

    public StockMovementReason Reason { get; }

    /// <summary>Lowest current stock that allows the change (removing 3 needs at least 3).</summary>
    public int MinCurrentStock { get; }

    /// <summary>Highest current stock that allows the change (adding 5 needs at most the maximum minus 5).</summary>
    public int MaxCurrentStock { get; }

    public string PerformedBy { get; }

    /// <inheritdoc cref="StockMovement.RequestKey"/>
    public string? RequestKey { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Plans a change of <paramref name="delta"/> units, or returns <c>null</c> when no current stock allows it (the
    /// change alone exceeds the maximum). The ledger fields are validated like <see cref="StockMovement.Record"/>.
    /// </summary>
    /// <exception cref="DomainException">The delta is zero, or the actor or request key is invalid.</exception>
    public static StockAdjustment? Plan(int delta, string performedBy, string? requestKey, DateTimeOffset at)
    {
        if (delta == 0)
        {
            throw new DomainException("A stock adjustment must change the stock.");
        }

        var validPerformedBy = StockMovement.GuardPerformedBy(performedBy);
        StockMovement.GuardRequestKey(requestKey);

        // Computed here, in C#, so the SQL never evaluates "stock + delta" and cannot overflow.
        var minimumCurrentStock = delta < 0 ? -(long)delta : 0L;
        var maximumCurrentStock = (long)ProductRules.MaxStockQuantity - Math.Max(delta, 0);
        if (minimumCurrentStock > ProductRules.MaxStockQuantity || maximumCurrentStock < 0)
        {
            return null;
        }

        return new StockAdjustment(
            delta,
            (int)minimumCurrentStock,
            (int)maximumCurrentStock,
            validPerformedBy,
            requestKey,
            at);
    }
}
