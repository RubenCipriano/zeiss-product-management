namespace ProductManagement.Application.Products.Contracts;

/// <summary>One entry of a product's stock history.</summary>
/// <param name="QuantityChange">Positive for additions, negative for removals.</param>
/// <param name="StockAfter">The product's stock right after this movement.</param>
/// <param name="Reason"><c>InitialStock</c>, <c>StockAdded</c>, <c>StockRemoved</c> or <c>Correction</c>.</param>
/// <param name="PerformedBy">The API consumer that made the change.</param>
public sealed record StockMovementResponse(
    long Id,
    int QuantityChange,
    int StockAfter,
    string Reason,
    string PerformedBy,
    DateTimeOffset OccurredAt);
