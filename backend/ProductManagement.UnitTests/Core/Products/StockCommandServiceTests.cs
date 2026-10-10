using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ProductManagement.Core.Common.Errors;
using ProductManagement.Core.Common.Requests;
using ProductManagement.Core.Common.Security;
using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.UnitTests.Core.Products;

/// <summary>
/// The stock use cases: quantity checks, the adjustment handed to persistence, and how its outcome becomes an answer.
/// The atomic update itself is covered by the integration tests.
/// </summary>
public class StockCommandServiceTests
{
    private const int ProductId = 123_456;
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductQueries _queries = Substitute.For<IProductQueries>();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();
    private readonly IRequestContext _request = Substitute.For<IRequestContext>();
    private readonly StockCommandService _service;

    public StockCommandServiceTests()
    {
        _actor.Name.Returns("maria");
        _request.IdempotencyKey.Returns((string?)null);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 10));

        _service = new StockCommandService(
            _repository,
            _queries,
            _actor,
            _request,
            new FakeTimeProvider(Now),
            NullLogger<StockCommandService>.Instance);
    }

    [Theory]
    [InlineData(ProductRules.MinAdjustmentQuantity - 1)]
    [InlineData(-5)]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    public async Task StockOperations_RejectInvalidQuantitiesBeforeReachingTheDatabase(int quantity)
    {
        var remove = await _service.DecrementStockAsync(ProductId, quantity, TestContext.Current.CancellationToken);
        var add = await _service.AddToStockAsync(ProductId, quantity, TestContext.Current.CancellationToken);

        remove.Error!.ValidationErrors.ShouldContainKey("quantity");
        add.Error!.ValidationErrors.ShouldContainKey("quantity");
        await _repository.DidNotReceiveWithAnyArgs().TryAdjustStockAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecrementStock_PlansANegativeAdjustmentByTheActorAndReturnsTheUpdatedProduct()
    {
        _repository.TryAdjustStockAsync(ProductId, Arg.Any<StockAdjustment>(), Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.Applied);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 7));

        var result = await _service.DecrementStockAsync(ProductId, 3, TestContext.Current.CancellationToken);

        result.Value.StockQuantity.ShouldBe(7);
        await _repository.Received(1).TryAdjustStockAsync(
            ProductId,
            Arg.Is<StockAdjustment>(adjustment =>
                adjustment.Delta == -3
                && adjustment.Reason == StockMovementReason.StockRemoved
                && adjustment.MinCurrentStock == 3
                && adjustment.PerformedBy == "maria"
                && adjustment.RequestKey == null
                && adjustment.OccurredAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecrementStock_AlreadyAppliedByTheSameRequest_ReportsSuccessWithoutChangingAnything()
    {
        _request.IdempotencyKey.Returns("maria:order-7");
        _repository.TryAdjustStockAsync(
                ProductId,
                Arg.Is<StockAdjustment>(adjustment => adjustment.RequestKey == "maria:order-7"),
                Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.AlreadyApplied);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 7));

        var result = await _service.DecrementStockAsync(ProductId, 3, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.StockQuantity.ShouldBe(7);
    }

    [Fact]
    public async Task DecrementStock_WithAKeyAlreadyRecordedOnAnotherChange_ReportsTheKeyAsReused()
    {
        _request.IdempotencyKey.Returns("maria:order-7");
        _repository.TryAdjustStockAsync(ProductId, Arg.Any<StockAdjustment>(), Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.RequestKeyReused);

        var result = await _service.DecrementStockAsync(ProductId, 3, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("idempotency_key_reused");
        result.Error.Type.ShouldBe(ErrorType.Unprocessable);
    }

    [Fact]
    public async Task DecrementStock_WhenTheRuleRejectsTheChange_ReportsInsufficientStockWithTheAvailableQuantity()
    {
        _repository.TryAdjustStockAsync(ProductId, Arg.Any<StockAdjustment>(), Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.NotApplied);

        var result = await _service.DecrementStockAsync(ProductId, 11, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("product.insufficient_stock");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Metadata["available"].ShouldBe(10);
        result.Error.Metadata["requested"].ShouldBe(11);
    }

    [Fact]
    public async Task AddToStock_WhenTheRuleRejectsTheChange_ReportsStockLimitExceeded()
    {
        _repository.TryAdjustStockAsync(ProductId, Arg.Any<StockAdjustment>(), Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.NotApplied);

        var result = await _service.AddToStockAsync(ProductId, ProductRules.MaxStockQuantity, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("product.stock_limit_exceeded");
    }

    [Fact]
    public async Task StockOperations_OnMissingProduct_ReturnNotFound()
    {
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns((ProductResponse?)null);

        var result = await _service.AddToStockAsync(ProductId, 1, TestContext.Current.CancellationToken);

        result.Error!.Type.ShouldBe(ErrorType.NotFound);
    }

    private static ProductResponse Response(int stock) =>
        new(ProductId, "Frame", null, 179m, stock, 2, "Frames", Now, Now, Version: 1);
}
