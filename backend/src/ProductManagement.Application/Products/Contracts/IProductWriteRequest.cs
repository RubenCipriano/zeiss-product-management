namespace ProductManagement.Application.Products.Contracts;

/// <summary>
/// Fields shared by create and update requests, so both are validated by the same rules.
/// </summary>
/// <remarks>
/// Value-type fields are nullable on purpose: a missing field shows up as a clear "must not be empty" error.
/// Otherwise it would silently deserialize to 0, and a missing stock quantity would be accepted as zero.
/// </remarks>
public interface IProductWriteRequest
{
    string? Name { get; }

    string? Description { get; }

    decimal? Price { get; }

    int? StockQuantity { get; }

    int? CategoryId { get; }
}
