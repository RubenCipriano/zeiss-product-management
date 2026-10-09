using ProductManagement.Domain.Common;
using ProductManagement.Domain.Products;

namespace ProductManagement.Domain.UnitTests.Products;

public sealed class StockMovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForInitialStock_RecordsAllOfTheStockAsOfTheCreation()
    {
        var product = Product.Create(123_456, "Frame", null, 10m, 15, 1, Now);

        var movement = StockMovement.ForInitialStock(product, "maria", "maria:create-1");

        movement.ProductId.ShouldBe(123_456);
        movement.QuantityChange.ShouldBe(15);
        movement.StockAfter.ShouldBe(15);
        movement.Reason.ShouldBe(StockMovementReason.InitialStock);
        movement.PerformedBy.ShouldBe("maria");
        movement.OccurredAt.ShouldBe(Now);
        movement.RequestKey.ShouldBe("maria:create-1");
    }

    [Fact]
    public void ForInitialStock_OfAnEmptyProduct_IsStillRecorded()
    {
        var product = Product.Create(123_456, "Frame", null, 10m, 0, 1, Now);

        StockMovement.ForInitialStock(product, "seed", requestKey: null).QuantityChange.ShouldBe(0);
    }

    [Fact]
    public void ForCorrection_RecordsTheDifferenceFromTheStockBeforeTheEdit()
    {
        var product = Product.Create(123_456, "Frame", null, 10m, 10, 1, Now);
        var later = Now.AddHours(1);
        product.Update("Frame", null, 10m, 4, 1, later);

        var movement = StockMovement.ForCorrection(product, stockBefore: 10, "maria", later)!;

        movement.QuantityChange.ShouldBe(-6);
        movement.StockAfter.ShouldBe(4);
        movement.Reason.ShouldBe(StockMovementReason.Correction);
        movement.OccurredAt.ShouldBe(later);
        movement.RequestKey.ShouldBeNull();
    }

    [Fact]
    public void ForCorrection_WhenTheStockDidNotChange_RecordsNothing()
    {
        var product = Product.Create(123_456, "Frame", null, 10m, 10, 1, Now);

        StockMovement.ForCorrection(product, stockBefore: 10, "maria", Now).ShouldBeNull();
    }

    [Fact]
    public void Record_TrimsTheActor()
    {
        StockMovement.Record(123_456, 1, 1, StockMovementReason.StockAdded, "  maria ", Now).PerformedBy.ShouldBe("maria");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_RejectsAMissingActor(string performedBy)
    {
        Should.Throw<DomainException>(() =>
            StockMovement.Record(123_456, 1, 1, StockMovementReason.StockAdded, performedBy, Now));
    }

    [Fact]
    public void Record_RejectsAnActorOrRequestKeyLongerThanTheColumns()
    {
        Should.Throw<DomainException>(() => StockMovement.Record(
            123_456, 1, 1, StockMovementReason.StockAdded, new string('a', StockMovement.PerformedByMaxLength + 1), Now));
        Should.Throw<DomainException>(() => StockMovement.Record(
            123_456, 1, 1, StockMovementReason.StockAdded, "maria", Now, new string('k', StockMovement.RequestKeyMaxLength + 1)));
    }

    [Fact]
    public void Record_RejectsNegativeStock()
    {
        Should.Throw<DomainException>(() =>
            StockMovement.Record(123_456, -1, -1, StockMovementReason.StockRemoved, "maria", Now));
    }
}
