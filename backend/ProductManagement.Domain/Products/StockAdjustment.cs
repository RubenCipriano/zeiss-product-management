using ProductManagement.Domain.Common;

namespace ProductManagement.Domain.Products;

/// <summary>
/// A planned change of a product's stock by <see cref="Delta"/> units (positive or negative), together with the ledger
/// entry it produces and the stock rule it must satisfy: the current stock has to lie between
/// <see cref="MinCurrentStock"/> and <see cref="MaxCurrentStock"/>, so that the result stays between zero and
/// <see cref="ProductRules.MaxStockQuantity"/>.
/// </summary>
/// <remarks>
/// The rule is a range rather than a check against a loaded product so that persistence can put it into a single
/// conditional <c>UPDATE</c>: check and write then happen atomically in the database, which keeps stock correct when
/// many requests change the same product at once. See docs/adr/0003.
/// </remarks>
public sealed class StockAdjustment
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
