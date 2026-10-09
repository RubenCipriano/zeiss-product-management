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
        GuardRequestKey(requestKey);

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
            PerformedBy = GuardPerformedBy(performedBy),
            OccurredAt = occurredAt,
            RequestKey = requestKey,
        };
    }

    /// <summary>The first entry of a newly created product: all of its stock, as of its creation.</summary>
    public static StockMovement ForInitialStock(Product product, string performedBy, string? requestKey) =>
        Record(
            product.Id,
            product.StockQuantity,
            product.StockQuantity,
            StockMovementReason.InitialStock,
            performedBy,
            product.CreatedAt,
            requestKey);

    /// <summary>
    /// The entry for an edit that overwrote the stock (from <paramref name="stockBefore"/> to the product's current
    /// stock), so the ledger keeps adding up; <c>null</c> when the edit left the stock unchanged.
    /// </summary>
    public static StockMovement? ForCorrection(Product product, int stockBefore, string performedBy, DateTimeOffset at) =>
        product.StockQuantity == stockBefore
            ? null
            : Record(
                product.Id,
                product.StockQuantity - stockBefore,
                product.StockQuantity,
                StockMovementReason.Correction,
                performedBy,
                at);

    internal static string GuardPerformedBy(string performedBy) =>
        Guard.AgainstInvalidText(performedBy, PerformedByMaxLength, nameof(PerformedBy));

    internal static void GuardRequestKey(string? requestKey)
    {
        if (requestKey is { Length: > RequestKeyMaxLength })
        {
            throw new DomainException($"The request key cannot exceed {RequestKeyMaxLength} characters.");
        }
    }
}
