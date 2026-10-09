using ProductManagement.Domain.Common;
using ProductManagement.Domain.Products;

namespace ProductManagement.Domain.UnitTests.Products;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithValidData_SetsAllProperties()
    {
        var product = Product.Create(123_456, "Progressive Lens", "Premium lens", 249.90m, 15, 1, Now);

        product.Id.ShouldBe(123_456);
        product.Name.ShouldBe("Progressive Lens");
        product.Description.ShouldBe("Premium lens");
        product.Price.ShouldBe(249.90m);
        product.StockQuantity.ShouldBe(15);
        product.CategoryId.ShouldBe(1);
        product.CreatedAt.ShouldBe(Now);
        product.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Create_TrimsTextAndNormalisesBlankDescriptionToNull()
    {
        var product = Product.Create(123_456, "  Frame  ", "   ", 10m, 0, 1, Now);

        product.Name.ShouldBe("Frame");
        product.Description.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProductRules.MinId)]
    [InlineData(ProductRules.MaxId)]
    public void Create_AcceptsIdentifiersAtTheSixDigitBoundaries(int id)
    {
        Product.Create(id, "Frame", null, 10m, 0, 1, Now).Id.ShouldBe(id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProductRules.MinId - 1)]
    [InlineData(ProductRules.MaxId + 1)]
    public void Create_RejectsIdentifiersThatAreNotSixDigits(int id)
    {
        Should.Throw<DomainException>(() => Product.Create(id, "Frame", null, 10m, 0, 1, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsMissingName(string? name)
    {
        Should.Throw<DomainException>(() => Product.Create(123_456, name!, null, 10m, 0, 1, Now));
    }

    [Fact]
    public void Create_RejectsNameLongerThanTheMaximum()
    {
        var name = new string('a', ProductRules.NameMaxLength + 1);

        Should.Throw<DomainException>(() => Product.Create(123_456, name, null, 10m, 0, 1, Now));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000.01")]
    [InlineData("10.001")]
    public void Create_RejectsInvalidPrice(string price)
    {
        Should.Throw<DomainException>(() =>
            Product.Create(123_456, "Frame", null, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), 0, 1, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    public void Create_RejectsStockOutsideTheAllowedRange(int stock)
    {
        Should.Throw<DomainException>(() => Product.Create(123_456, "Frame", null, 10m, stock, 1, Now));
    }

    [Fact]
    public void Update_ReplacesDetailsAndKeepsCreationTime()
    {
        var product = Product.Create(123_456, "Frame", null, 10m, 5, 1, Now);
        var later = Now.AddHours(1);

        product.Update("Titanium Frame", "Lightweight", 179m, 7, 2, later);

        product.Name.ShouldBe("Titanium Frame");
        product.Description.ShouldBe("Lightweight");
        product.Price.ShouldBe(179m);
        product.StockQuantity.ShouldBe(7);
        product.CategoryId.ShouldBe(2);
        product.CreatedAt.ShouldBe(Now);
        product.UpdatedAt.ShouldBe(later);
    }

    [Fact]
    public void Update_WithInvalidData_LeavesTheProductUnchanged()
    {
        var product = Product.Create(123_456, "Frame", "Original", 10m, 5, 1, Now);

        // Name is valid but price is not: nothing may be applied.
        Should.Throw<DomainException>(() => product.Update("New name", "New", -5m, 5, 1, Now.AddHours(1)));

        product.Name.ShouldBe("Frame");
        product.Description.ShouldBe("Original");
        product.UpdatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(10, -10, true)] // remove everything
    [InlineData(10, -11, false)] // oversell
    [InlineData(0, -1, false)]
    [InlineData(0, 1, true)]
    [InlineData(5, 0, true)]
    [InlineData(ProductRules.MaxStockQuantity - 5, 5, true)] // reach the maximum exactly
    [InlineData(ProductRules.MaxStockQuantity - 5, 6, false)] // exceed the maximum
    [InlineData(ProductRules.MaxStockQuantity, int.MaxValue, false)] // would overflow in naive arithmetic
    [InlineData(ProductRules.MaxStockQuantity, int.MinValue, false)]
    public void CanAdjustStockBy_KeepsStockBetweenZeroAndTheMaximum(int currentStock, int delta, bool expected)
    {
        var product = Product.Create(123_456, "Frame", null, 10m, currentStock, 1, Now);

        var rule = Product.CanAdjustStockBy(delta).Compile();

        rule(product).ShouldBe(expected);
    }
}
