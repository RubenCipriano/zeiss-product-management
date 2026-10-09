using ProductManagement.Domain.Common;

namespace ProductManagement.Domain.Products;

/// <summary>Why a product's stock changed.</summary>
public enum StockMovementReason
{
    /// <summary>The stock the product was created with.</summary>
    InitialStock,

    /// <summary>A delivery (add-to-stock).</summary>
    StockAdded,

    /// <summary>A sale or write-off (decrement-stock).</summary>
    StockRemoved,

    /// <summary>The stock was overwritten when editing the product.</summary>
    Correction,
}

/// <summary>
/// One entry in a product's stock ledger: how much the stock changed, what it became, why, who did it and when.
/// Append-only. For every product, the sum of its movements equals its current stock.
/// </summary>
public sealed class StockMovement
{
    public const int PerformedByMaxLength = 100;

    // Required by EF Core for materialization.
    private StockMovement()
    {
        PerformedBy = string.Empty;
    }

    public long Id { get; private set; }

    public int ProductId { get; private set; }

    public int QuantityChange { get; private set; }

    public int StockAfter { get; private set; }

    public StockMovementReason Reason { get; private set; }

    public string PerformedBy { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static StockMovement Record(
        int productId,
        int quantityChange,
        int stockAfter,
        StockMovementReason reason,
        string performedBy,
        DateTimeOffset occurredAt)
    {
        if (stockAfter < 0)
        {
            throw new DomainException("Stock cannot become negative.");
        }

        return new StockMovement
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            StockAfter = stockAfter,
            Reason = reason,
            PerformedBy = Guard.AgainstInvalidText(performedBy, PerformedByMaxLength, nameof(PerformedBy)),
            OccurredAt = occurredAt,
        };
    }
}
