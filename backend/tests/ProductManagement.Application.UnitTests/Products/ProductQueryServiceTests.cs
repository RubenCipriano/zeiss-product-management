using NSubstitute;
using ProductManagement.Application.Common.Errors;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Application.Products.Validation;

namespace ProductManagement.Application.UnitTests.Products;

public sealed class ProductQueryServiceTests
{
    private readonly IProductQueries _queries = Substitute.For<IProductQueries>();
    private readonly ProductQueryService _service;

    public ProductQueryServiceTests() =>
        _service = new ProductQueryService(
            _queries,
            new PageQueryValidator(),
            new ProductSearchQueryValidator(),
            new StockLevelQueryValidator());

    [Fact]
    public async Task GetById_WhenMissing_ReturnsNotFound()
    {
        var result = await _service.GetByIdAsync(123_456, TestContext.Current.CancellationToken);

        result.Error!.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe("product.not_found");
    }

    [Fact]
    public async Task Search_TrimsTheTermBeforeQuerying()
    {
        _queries.SearchByNameAsync(Arg.Any<string>(), Arg.Any<PageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResponse<ProductResponse>([], 1, 20, 0));

        await _service.SearchByNameAsync(new ProductSearchQuery { Name = "  lens " }, TestContext.Current.CancellationToken);

        await _queries.Received(1).SearchByNameAsync("lens", Arg.Any<PageQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_WithInvalidPaging_ReturnsValidationErrorAndDoesNotQuery()
    {
        var result = await _service.ListAsync(new PageQuery { Page = 0, PageSize = 500 }, TestContext.Current.CancellationToken);

        result.Error!.ValidationErrors.Keys.ShouldBe(["page", "pageSize"], ignoreOrder: true);
        await _queries.DidNotReceiveWithAnyArgs().ListAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StockLevel_PassesBoundsThrough()
    {
        _queries.ListByStockLevelAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResponse<ProductResponse>([], 1, 20, 0));

        var result = await _service.ListByStockLevelAsync(new StockLevelQuery { Min = 2, Max = 8 }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _queries.Received(1).ListByStockLevelAsync(2, 8, Arg.Any<PageQuery>(), Arg.Any<CancellationToken>());
    }
}
