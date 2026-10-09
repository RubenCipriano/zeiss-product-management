using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Errors;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Common.Security;
using ProductManagement.Application.Products;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Application.Products.Validation;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.UnitTests.Products;

/// <summary>
/// Orchestration rules of the write use cases: what is validated, in which order, what is persisted, and how
/// persistence outcomes become errors. Persistence is replaced by substitutes; real database behaviour is covered
/// by the integration tests.
/// </summary>
public sealed class ProductCommandServiceTests
{
    private const int ProductId = 123_456;
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductQueries _queries = Substitute.For<IProductQueries>();
    private readonly ICategoryQueries _categories = Substitute.For<ICategoryQueries>();
    private readonly IProductIdGenerator _idGenerator = Substitute.For<IProductIdGenerator>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();
    private readonly ProductCommandService _service;

    public ProductCommandServiceTests()
    {
        _categories.ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _actor.Name.Returns("maria");
        _actor.RequestKey.Returns((string?)null);
        _idGenerator.NextIdAsync(Arg.Any<CancellationToken>()).Returns(ProductId);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 10));

        _service = new ProductCommandService(
            _repository,
            _queries,
            _categories,
            _idGenerator,
            _unitOfWork,
            _actor,
            new CreateProductRequestValidator(),
            new UpdateProductRequestValidator(),
            new FakeTimeProvider(Now),
            NullLogger<ProductCommandService>.Instance);
    }

    [Fact]
    public async Task Create_WithValidRequest_AssignsGeneratedIdPersistsAndReturnsTheStoredProduct()
    {
        var result = await _service.CreateAsync(ValidCreateRequest(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(ProductId);
        _repository.Received(1).Add(Arg.Is<Product>(product =>
            product.Id == ProductId && product.Name == "Frame" && product.CreatedAt == Now));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_RecordsTheInitialStockInTheLedgerInTheSameSave()
    {
        await _service.CreateAsync(ValidCreateRequest(), TestContext.Current.CancellationToken);

        _repository.Received(1).AddMovement(Arg.Is<StockMovement>(movement =>
            movement.ProductId == ProductId
            && movement.QuantityChange == 10
            && movement.StockAfter == 10
            && movement.Reason == StockMovementReason.InitialStock
            && movement.PerformedBy == "maria"));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ThatChangesTheStock_RecordsACorrection()
    {
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(ExistingProduct()); // stock 10

        await _service.UpdateAsync(ProductId, ValidUpdateRequest() with { StockQuantity = 4 }, null, TestContext.Current.CancellationToken);

        _repository.Received(1).AddMovement(Arg.Is<StockMovement>(movement =>
            movement.QuantityChange == -6 && movement.StockAfter == 4 && movement.Reason == StockMovementReason.Correction));
    }

    [Fact]
    public async Task Update_ThatKeepsTheStock_RecordsNothing()
    {
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(ExistingProduct()); // stock 10

        await _service.UpdateAsync(ProductId, ValidUpdateRequest() with { Name = "Renamed" }, null, TestContext.Current.CancellationToken);

        _repository.DidNotReceiveWithAnyArgs().AddMovement(default!);
    }

    [Fact]
    public async Task Create_WithInvalidRequest_ReturnsValidationErrorsWithoutTouchingPersistence()
    {
        var request = ValidCreateRequest() with { Name = " ", Price = null };

        var result = await _service.CreateAsync(request, TestContext.Current.CancellationToken);

        result.Error!.Type.ShouldBe(ErrorType.Validation);
        result.Error.ValidationErrors.Keys.ShouldBe(["name", "price"], ignoreOrder: true);
        await _idGenerator.DidNotReceiveWithAnyArgs().NextIdAsync(Arg.Any<CancellationToken>());
        _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Create_WithUnknownCategory_ReturnsValidationErrorAndDoesNotConsumeAnId()
    {
        _categories.ExistsAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.CreateAsync(ValidCreateRequest() with { CategoryId = 99 }, TestContext.Current.CancellationToken);

        result.Error!.ValidationErrors.ShouldContainKey("categoryId");
        await _idGenerator.DidNotReceiveWithAnyArgs().NextIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WhenProductDoesNotExist_ReturnsNotFound()
    {
        var result = await _service.UpdateAsync(ProductId, ValidUpdateRequest(), null, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("product.not_found");
    }

    [Fact]
    public async Task Update_WithStaleExpectedVersion_ReturnsPreconditionFailedWithoutSaving()
    {
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(ExistingProduct());

        var result = await _service.UpdateAsync(ProductId, ValidUpdateRequest(), expectedVersion: 42, TestContext.Current.CancellationToken);

        result.Error!.Type.ShouldBe(ErrorType.PreconditionFailed);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WithMatchingVersion_AppliesChangesAndSaves()
    {
        var product = ExistingProduct();
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(product);

        var result = await _service.UpdateAsync(
            ProductId,
            ValidUpdateRequest() with { Name = "Renamed" },
            expectedVersion: product.Version,
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        product.Name.ShouldBe("Renamed");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "product.concurrent_modification", ErrorType.Conflict)]
    [InlineData(0u, "product.version_mismatch", ErrorType.PreconditionFailed)]
    public async Task Update_WhenRowChangesBetweenReadAndWrite_MapsTheConflictByWhetherTheClientSentAVersion(
        uint? expectedVersion,
        string expectedCode,
        ErrorType expectedType)
    {
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(ExistingProduct());
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new ConcurrencyConflictException()));

        var result = await _service.UpdateAsync(ProductId, ValidUpdateRequest(), expectedVersion, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(expectedCode);
        result.Error.Type.ShouldBe(expectedType);
    }

    [Fact]
    public async Task Update_ToUnknownCategory_ReturnsValidationError()
    {
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(ExistingProduct());
        _categories.ExistsAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.UpdateAsync(ProductId, ValidUpdateRequest() with { CategoryId = 99 }, null, TestContext.Current.CancellationToken);

        result.Error!.ValidationErrors.ShouldContainKey("categoryId");
    }

    [Fact]
    public async Task Delete_ExistingProduct_RemovesAndSaves()
    {
        var product = ExistingProduct();
        _repository.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(product);

        var result = await _service.DeleteAsync(ProductId, null, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _repository.Received(1).Remove(product);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_MissingProduct_ReturnsNotFound()
    {
        var result = await _service.DeleteAsync(ProductId, null, TestContext.Current.CancellationToken);

        result.Error!.Type.ShouldBe(ErrorType.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    public async Task StockOperations_RejectInvalidQuantitiesBeforeReachingTheDatabase(int quantity)
    {
        var remove = await _service.DecrementStockAsync(ProductId, quantity, TestContext.Current.CancellationToken);
        var add = await _service.AddToStockAsync(ProductId, quantity, TestContext.Current.CancellationToken);

        remove.Error!.ValidationErrors.ShouldContainKey("quantity");
        add.Error!.ValidationErrors.ShouldContainKey("quantity");
        await _repository.DidNotReceiveWithAnyArgs().TryAdjustStockAsync(default, default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecrementStock_SendsANegativeDeltaAndReturnsTheUpdatedProduct()
    {
        _repository.TryAdjustStockAsync(ProductId, -3, "maria", null, Now, Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.Applied);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 7));

        var result = await _service.DecrementStockAsync(ProductId, 3, TestContext.Current.CancellationToken);

        result.Value.StockQuantity.ShouldBe(7);
    }

    [Fact]
    public async Task DecrementStock_AlreadyAppliedByTheSameRequest_ReportsSuccessWithoutChangingAnything()
    {
        _actor.RequestKey.Returns("maria:order-7");
        _repository.TryAdjustStockAsync(ProductId, -3, "maria", "maria:order-7", Now, Arg.Any<CancellationToken>())
            .Returns(StockAdjustmentOutcome.AlreadyApplied);
        _queries.GetByIdAsync(ProductId, Arg.Any<CancellationToken>()).Returns(Response(stock: 7));

        var result = await _service.DecrementStockAsync(ProductId, 3, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.StockQuantity.ShouldBe(7);
    }

    [Fact]
    public async Task DecrementStock_WhenTheRuleRejectsTheChange_ReportsInsufficientStockWithTheAvailableQuantity()
    {
        _repository.TryAdjustStockAsync(ProductId, -11, "maria", null, Now, Arg.Any<CancellationToken>())
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
        _repository.TryAdjustStockAsync(ProductId, ProductRules.MaxStockQuantity, "maria", null, Now, Arg.Any<CancellationToken>())
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

    private static CreateProductRequest ValidCreateRequest() => new()
    {
        Name = "Frame",
        Description = "Titanium",
        Price = 179m,
        StockQuantity = 10,
        CategoryId = 2,
    };

    private static UpdateProductRequest ValidUpdateRequest() => new()
    {
        Name = "Frame",
        Description = "Titanium",
        Price = 179m,
        StockQuantity = 10,
        CategoryId = 2,
    };

    private static Product ExistingProduct() => Product.Create(ProductId, "Frame", null, 150m, 10, 2, Now.AddDays(-1));

    private static ProductResponse Response(int stock) =>
        new(ProductId, "Frame", null, 179m, stock, 2, "Frames", Now, Now, Version: 1);
}
