using ProductManagement.Api.Idempotency;

namespace ProductManagement.UnitTests.Api.Idempotency;

public class IdempotencyKeyTests
{
    [Theory]
    [InlineData("order-7")]
    [InlineData("3f2b8c1e-0d4a-4c55-9b1e-1f6f0e6a9c2d")]
    [InlineData("a.b_c:d")]
    public void AValidKey_IsScopedToTheCaller(string raw)
    {
        IdempotencyKey.TryParse(raw, "maria", out var scopedKey).ShouldBeTrue();
        scopedKey.ShouldBe($"maria:{raw}");
    }

    [Fact]
    public void TheSameKeyFromTwoCallers_GivesTwoDifferentKeys()
    {
        IdempotencyKey.TryParse("order-7", "maria", out var maria);
        IdempotencyKey.TryParse("order-7", "joao", out var joao);

        maria.ShouldNotBe(joao);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("ümlaut")]
    [InlineData("a,b")]
    public void AKeyWithOtherCharacters_IsRejected(string raw)
    {
        IdempotencyKey.TryParse(raw, "maria", out var scopedKey).ShouldBeFalse();
        scopedKey.ShouldBeNull();
    }

    [Fact]
    public void TheLengthIsLimited()
    {
        IdempotencyKey.TryParse(new string('k', IdempotencyKey.MaxLength), "maria", out _).ShouldBeTrue();
        IdempotencyKey.TryParse(new string('k', IdempotencyKey.MaxLength + 1), "maria", out _).ShouldBeFalse();
    }
}
