using ProductManagement.Domain.Common;
using ProductManagement.Domain.Products;

namespace ProductManagement.Domain.UnitTests.Products;

public sealed class StockAdjustmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, -10, true)] // remove everything
    [InlineData(10, -11, false)] // oversell
    [InlineData(0, -1, false)]
    [InlineData(0, 1, true)]
    [InlineData(ProductRules.MaxStockQuantity - 5, 5, true)] // reach the maximum exactly
    [InlineData(ProductRules.MaxStockQuantity - 5, 6, false)] // exceed the maximum
    [InlineData(ProductRules.MaxStockQuantity, int.MaxValue, false)] // would overflow in naive arithmetic
    [InlineData(ProductRules.MaxStockQuantity, int.MinValue, false)]
    public void Plan_KeepsStockBetweenZeroAndTheMaximum(int currentStock, int delta, bool expected)
    {
        var adjustment = StockAdjustment.Plan(delta, "maria", null, Now);

        var allowed = adjustment is not null
            && currentStock >= adjustment.MinCurrentStock
            && currentStock <= adjustment.MaxCurrentStock;
        allowed.ShouldBe(expected);
    }

    [Theory]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    [InlineData(-ProductRules.MaxStockQuantity - 1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Plan_ReturnsNothingWhenNoStockAllowsTheChange(int delta)
    {
        StockAdjustment.Plan(delta, "maria", null, Now).ShouldBeNull();
    }

    [Theory]
    [InlineData(-3, StockMovementReason.StockRemoved)]
    [InlineData(5, StockMovementReason.StockAdded)]
    public void Plan_DescribesTheLedgerEntry(int delta, StockMovementReason reason)
    {
        var adjustment = StockAdjustment.Plan(delta, "  maria ", "maria:order-7", Now)!;

        adjustment.Delta.ShouldBe(delta);
        adjustment.Reason.ShouldBe(reason);
        adjustment.PerformedBy.ShouldBe("maria");
        adjustment.RequestKey.ShouldBe("maria:order-7");
        adjustment.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Plan_RejectsAChangeOfNothing()
    {
        Should.Throw<DomainException>(() => StockAdjustment.Plan(0, "maria", null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_RejectsAMissingActor(string performedBy)
    {
        Should.Throw<DomainException>(() => StockAdjustment.Plan(1, performedBy, null, Now));
    }

    [Fact]
    public void Plan_AppliesTheSameLimitsAsTheLedger()
    {
        var tooLongActor = new string('a', StockMovement.PerformedByMaxLength + 1);
        var tooLongKey = new string('k', StockMovement.RequestKeyMaxLength + 1);

        Should.Throw<DomainException>(() => StockAdjustment.Plan(1, tooLongActor, null, Now));
        Should.Throw<DomainException>(() => StockAdjustment.Plan(1, "maria", tooLongKey, Now));
        StockAdjustment.Plan(1, new string('a', StockMovement.PerformedByMaxLength), new string('k', StockMovement.RequestKeyMaxLength), Now)
            .ShouldNotBeNull();
    }
}
