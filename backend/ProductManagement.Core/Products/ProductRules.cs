namespace ProductManagement.Core.Products;

/// <summary>
/// The product limits (id range, name length, price range, maximum stock, ...). The single source for request
/// validation, the <see cref="Product"/> class and the database check constraints, so all three always agree.
/// </summary>
public static class ProductRules
{
    /// <summary>Smallest identifier the system may assign (identifiers are exactly six digits).</summary>
    public const int MinId = 100_000;

    /// <summary>Largest identifier the system may assign.</summary>
    public const int MaxId = 999_999;

    public const int NameMaxLength = 200;

    public const int DescriptionMaxLength = 2_000;

    public const decimal MinPrice = 0.01m;

    public const decimal MaxPrice = 1_000_000m;

    /// <summary>Prices are stored with two decimal places (currency minor units).</summary>
    public const int PriceDecimalPlaces = 2;

    /// <summary>
    /// Upper bound for the quantity in stock. Guards against data-entry mistakes and makes integer overflow on
    /// stock increments impossible.
    /// </summary>
    public const int MaxStockQuantity = 1_000_000;

    /// <summary>Smallest quantity a single stock addition or removal may move.</summary>
    public const int MinAdjustmentQuantity = 1;

    public static bool IsValidId(int id) => id is >= MinId and <= MaxId;

    public static bool HasValidPricePrecision(decimal price) => decimal.Round(price, PriceDecimalPlaces) == price;
}
