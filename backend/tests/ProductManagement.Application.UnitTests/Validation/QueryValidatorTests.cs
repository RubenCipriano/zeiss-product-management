using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Application.Products.Validation;

namespace ProductManagement.Application.UnitTests.Validation;

public sealed class QueryValidatorTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, PageQuery.MaxPageSize, true)]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, PageQuery.MaxPageSize + 1, false)]
    public void PageQuery_EnforcesPageAndPageSizeLimits(int page, int pageSize, bool isValid)
    {
        new PageQueryValidator().Validate(new PageQuery { Page = page, PageSize = pageSize }).IsValid.ShouldBe(isValid);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("le", true)]
    public void Search_RequiresANonBlankName(string? name, bool isValid)
    {
        new ProductSearchQueryValidator().Validate(new ProductSearchQuery { Name = name }).IsValid.ShouldBe(isValid);
    }

    [Fact]
    public void Search_AlsoValidatesPaging()
    {
        new ProductSearchQueryValidator().Validate(new ProductSearchQuery { Name = "lens", PageSize = 0 })
            .Errors.ShouldContain(error => error.PropertyName == "PageSize");
    }

    [Theory]
    [InlineData(null, null, true)] // no bounds: every product
    [InlineData(5, null, true)]
    [InlineData(null, 5, true)]
    [InlineData(5, 5, true)] // single value range
    [InlineData(6, 5, false)] // inverted range
    [InlineData(-1, 5, false)]
    [InlineData(0, -1, false)]
    public void StockLevel_RequiresNonNegativeOrderedBounds(int? min, int? max, bool isValid)
    {
        new StockLevelQueryValidator().Validate(new StockLevelQuery { Min = min, Max = max }).IsValid.ShouldBe(isValid);
    }
}
