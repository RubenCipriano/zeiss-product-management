using ProductManagement.Api.Http;

namespace ProductManagement.Api.UnitTests.Http;

public sealed class EntityTagTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void IfMatch_AbsentOrAnyVersion_IsNoPrecondition(string? header)
    {
        EntityTag.TryParseIfMatch(header, out var version).ShouldBeTrue();
        version.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"0\"", 0u)]
    [InlineData(" \"123\" ", 123u)]
    [InlineData("\"4294967295\"", uint.MaxValue)]
    public void IfMatch_AStrongTag_IsTheExpectedVersion(string header, uint expected)
    {
        EntityTag.TryParseIfMatch(header, out var version).ShouldBeTrue();
        version.ShouldBe(expected);
    }

    [Theory]
    [InlineData("123")] // not quoted
    [InlineData("W/\"123\"")] // weak tags are not allowed for If-Match
    [InlineData("\"1\", \"2\"")] // lists are not supported
    [InlineData("\"\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"4294967296\"")] // beyond uint
    public void IfMatch_AnythingElse_IsRejected(string header)
    {
        EntityTag.TryParseIfMatch(header, out _).ShouldBeFalse();
    }

    [Fact]
    public void Format_ProducesAStrongTagThatParsesBack()
    {
        var tag = EntityTag.Format(42);

        tag.ShouldBe("\"42\"");
        EntityTag.TryParseIfMatch(tag, out var version).ShouldBeTrue();
        version.ShouldBe(42u);
    }
}
