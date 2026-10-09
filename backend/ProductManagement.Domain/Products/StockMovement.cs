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

    public const int RequestKeyMaxLength = 300;

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

    /// <summary>
    /// The idempotency key of the request that made this change (scoped to the consumer), if it sent one. Unique:
    /// the database refuses a second movement for the same request, so a request is never applied twice.
    /// </summary>
    public string? RequestKey { get; private set; }

    public static StockMovement Record(
        int productId,
        int quantityChange,
        int stockAfter,
        StockMovementReason reason,
        string performedBy,
        DateTimeOffset occurredAt,
        string? requestKey = null)
    {
        if (requestKey is { Length: > RequestKeyMaxLength })
        {
            throw new DomainException($"The request key cannot exceed {RequestKeyMaxLength} characters.");
        }

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
            RequestKey = requestKey,
        };
    }
}
