using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

public sealed class ProductQueryEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Search_MatchesPartOfTheNameIgnoringCase()
    {
        await CreateProductsAsync(("Progressive Lens", 5), ("Single Vision LENS", 3), ("Titanium Frame", 9));

        using var response = await Api.SearchAsync("lEnS");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await ReadPageAsync(response);
        page.Items.Select(product => (product.Name, product.StockQuantity))
            .ShouldBe([("Progressive Lens", 5), ("Single Vision LENS", 3)]);
    }

    [Theory]
    [InlineData("%", "50% Off Cleaning Kit")]
    [InlineData("_", "Lens_Wipes")]
    [InlineData("\\", "Back\\Slash Case")]
    public async Task Search_TreatsLikeWildcardsAsLiteralCharacters(string term, string expectedMatch)
    {
        await CreateProductsAsync(("50% Off Cleaning Kit", 1), ("Lens_Wipes", 1), ("Back\\Slash Case", 1), ("Plain Frame", 1));

        using var response = await Api.SearchAsync(term);

        (await ReadPageAsync(response)).Items.Select(product => product.Name).ShouldBe([expectedMatch]);
    }

    [Fact]
    public async Task Search_WithNoMatches_ReturnsAnEmptyPage()
    {
        await CreateProductsAsync(("Frame", 1));

        using var response = await Api.SearchAsync("sunglasses");

        var page = await ReadPageAsync(response);
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Search_WithoutAName_ReturnsBadRequest(string? name)
    {
        using var response = await Api.SearchAsync(name);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.ShouldContainKey("name");
    }

    [Fact]
    public async Task StockLevel_ReturnsProductsWithinTheInclusiveRangeOrderedByStock()
    {
        await CreateProductsAsync(("Zero", 0), ("Two", 2), ("Five", 5), ("Six", 6), ("Fifty", 50));

        using var response = await Api.ByStockLevelAsync(min: 2, max: 5);

        (await ReadPageAsync(response)).Items.Select(product => (product.Name, product.StockQuantity))
            .ShouldBe([("Two", 2), ("Five", 5)]);
    }

    [Theory]
    [InlineData(null, 2, new[] { "Zero", "Two" })]
    [InlineData(6, null, new[] { "Six", "Fifty" })]
    [InlineData(null, null, new[] { "Zero", "Two", "Six", "Fifty" })]
    public async Task StockLevel_TreatsAMissingBoundAsOpen(int? min, int? max, string[] expected)
    {
        await CreateProductsAsync(("Zero", 0), ("Two", 2), ("Six", 6), ("Fifty", 50));

        using var response = await Api.ByStockLevelAsync(min, max);

        (await ReadPageAsync(response)).Items.Select(product => product.Name).ShouldBe(expected);
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(-1, 2)]
    public async Task StockLevel_WithAnInvalidRange_ReturnsBadRequest(int min, int max)
    {
        using var response = await Api.ByStockLevelAsync(min, max);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Categories_AreSeededByTheMigration()
    {
        using var response = await Api.Http.GetAsync(new Uri("api/categories", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldContain("Spectacle Lenses");
        body.ShouldContain("Frames");
    }

    private async Task CreateProductsAsync(params (string Name, int Stock)[] products)
    {
        foreach (var (name, stock) in products)
        {
            await Api.CreateProductAsync(TestProducts.CreateRequest(name: name, stockQuantity: stock));
        }
    }
}
