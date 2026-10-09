using ProductManagement.Domain.Categories;
using ProductManagement.Domain.Common;

namespace ProductManagement.Domain.UnitTests.Categories;

public sealed class CategoryTests
{
    [Fact]
    public void Constructor_WithValidData_TrimsName()
    {
        var category = new Category(1, "  Frames ");

        category.Id.ShouldBe(1);
        category.Name.ShouldBe("Frames");
    }

    [Theory]
    [InlineData(0, "Frames")]
    [InlineData(1, " ")]
    public void Constructor_RejectsInvalidData(int id, string name)
    {
        Should.Throw<DomainException>(() => new Category(id, name));
    }
}
