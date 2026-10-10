using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Core.Products.Validation;

namespace ProductManagement.UnitTests.Core.Products.Validation;

public class ProductWriteRequestValidatorTests
{
    private readonly CreateProductRequestValidator _validator = new();

    private static CreateProductRequest Valid => new()
    {
        Name = "Frame",
        Description = null,
        Price = 0.01m,
        StockQuantity = 0,
        CategoryId = 1,
    };

    [Fact]
    public void AcceptsAValidRequestAtTheLowerBoundaries()
    {
        _validator.Validate(Valid).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AcceptsAValidRequestAtTheUpperBoundaries()
    {
        var request = Valid with
        {
            Name = new string('a', ProductRules.NameMaxLength),
            Description = new string('d', ProductRules.DescriptionMaxLength),
            Price = ProductRules.MaxPrice,
            StockQuantity = ProductRules.MaxStockQuantity,
        };

        _validator.Validate(request).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ReportsEveryMissingRequiredField()
    {
        var result = _validator.Validate(new CreateProductRequest());

        result.Errors.Select(error => error.PropertyName).Distinct()
            .ShouldBe(["Name", "Price", "StockQuantity", "CategoryId"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1000000.01")]
    [InlineData("1.999")]
    public void RejectsInvalidPrices(string price)
    {
        var request = Valid with { Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) };

        _validator.Validate(request).Errors.ShouldContain(error => error.PropertyName == "Price");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    public void RejectsStockOutsideTheAllowedRange(int stock)
    {
        _validator.Validate(Valid with { StockQuantity = stock }).Errors
            .ShouldContain(error => error.PropertyName == "StockQuantity");
    }

    [Fact]
    public void RejectsTooLongText()
    {
        var request = Valid with
        {
            Name = new string('a', ProductRules.NameMaxLength + 1),
            Description = new string('d', ProductRules.DescriptionMaxLength + 1),
        };

        var properties = _validator.Validate(request).Errors.Select(error => error.PropertyName).ToList();

        properties.ShouldContain("Name");
        properties.ShouldContain("Description");
    }

    [Fact]
    public void UpdateRequestsFollowTheSameRules()
    {
        var result = new UpdateProductRequestValidator().Validate(new UpdateProductRequest { Name = " ", Price = 0m });

        result.Errors.Select(error => error.PropertyName).Distinct()
            .ShouldBe(["Name", "Price", "StockQuantity", "CategoryId"], ignoreOrder: true);
    }
}
