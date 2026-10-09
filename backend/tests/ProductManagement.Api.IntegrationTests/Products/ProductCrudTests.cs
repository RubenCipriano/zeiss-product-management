using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

public sealed class ProductCrudTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Create_ReturnsCreatedWithLocationETagAndTheStoredRepresentation()
    {
        var request = TestProducts.CreateRequest(name: "  Titanium Frame ", stockQuantity: 7, price: 179.90m, categoryId: TestProducts.FramesCategoryId);

        using var response = await Api.CreateAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var product = await ReadAsync<ProductResponse>(response);
        product.Name.ShouldBe("Titanium Frame");
        product.Price.ShouldBe(179.90m);
        product.StockQuantity.ShouldBe(7);
        product.CategoryName.ShouldBe("Frames");
        response.Headers.Location!.AbsolutePath.ShouldBe($"/api/products/{product.Id}");
        response.ETag().ShouldBe($"\"{product.Version}\"");
    }

    [Fact]
    public async Task Create_AssignsSixDigitIdentifiers()
    {
        var first = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "First"));
        var second = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Second"));

        first.Id.ShouldBeInRange(100_000, 999_999);
        second.Id.ShouldBeInRange(100_000, 999_999);
        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task Create_WithInvalidFields_ReturnsProblemDetailsListingEveryInvalidField()
    {
        using var response = await Api.CreateAsync(new { name = "", price = 10.999m, stockQuantity = -1, categoryId = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ContentType()!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadProblemAsync(response);
        problem.ErrorCode.ShouldBe("validation_failed");
        problem.Errors!.Keys.ShouldBe(["name", "price", "stockQuantity"], ignoreOrder: true);
    }

    [Fact]
    public async Task Create_WithMissingFields_ReportsThemInsteadOfDefaultingToZero()
    {
        using var response = await Api.CreateAsync(new { name = "Frame", categoryId = 1 });

        var problem = await ReadProblemAsync(response);
        problem.Errors!.Keys.ShouldBe(["price", "stockQuantity"], ignoreOrder: true);
    }

    [Fact]
    public async Task Create_WithUnknownCategory_ReturnsValidationError()
    {
        using var response = await Api.CreateAsync(TestProducts.CreateRequest(categoryId: 999));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.ShouldContainKey("categoryId");
    }

    [Fact]
    public async Task Create_WithWrongJsonTypes_ReturnsValidationErrorWithoutLeakingInternals()
    {
        using var response = await Api.CreateAsync(new { name = "Frame", price = "12", stockQuantity = 1, categoryId = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldContain("$.price");
        body.ShouldNotContain("System.");
    }

    [Fact]
    public async Task GetById_ReturnsTheProductWithItsStockAndETag()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 12));

        using var response = await Api.GetAsync(created.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(response)).ShouldBe(created);
        response.ETag().ShouldBe($"\"{created.Version}\"");
    }

    [Fact]
    public async Task GetById_ForUnknownProduct_ReturnsNotFoundProblem()
    {
        using var response = await Api.GetAsync(999_999);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).ErrorCode.ShouldBe("product.not_found");
    }

    [Fact]
    public async Task List_IsPagedOrderedByNameAndIncludesStock()
    {
        await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Charlie", stockQuantity: 3));
        await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Alpha", stockQuantity: 1));
        await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Bravo", stockQuantity: 2));

        using var firstPage = await Api.ListAsync(page: 1, pageSize: 2);
        using var secondPage = await Api.ListAsync(page: 2, pageSize: 2);

        var page1 = await ReadPageAsync(firstPage);
        var page2 = await ReadPageAsync(secondPage);
        page1.Items.Select(product => (product.Name, product.StockQuantity)).ShouldBe([("Alpha", 1), ("Bravo", 2)]);
        page2.Items.Select(product => product.Name).ShouldBe(["Charlie"]);
        page1.TotalCount.ShouldBe(3);
        page1.TotalPages.ShouldBe(2);
    }

    [Fact]
    public async Task List_WithAPageNumberLargeEnoughToOverflowTheOffset_ReturnsBadRequestNotServerError()
    {
        using var response = await Api.ListAsync(page: int.MaxValue / 50, pageSize: 100);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.ShouldContainKey("page");
    }

    [Fact]
    public async Task List_WithInvalidPaging_ReturnsBadRequest()
    {
        using var response = await Api.ListAsync(page: 0, pageSize: 1000);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.Keys.ShouldBe(["page", "pageSize"], ignoreOrder: true);
    }

    [Fact]
    public async Task Update_ReplacesTheProductAndChangesItsVersion()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Old name"));
        var request = TestProducts.UpdateRequest(created, name: "New name", price: 10.50m, categoryId: TestProducts.FramesCategoryId);

        using var response = await Api.UpdateAsync(created.Id, request, ifMatch: $"\"{created.Version}\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await ReadAsync<ProductResponse>(response);
        updated.Name.ShouldBe("New name");
        updated.Price.ShouldBe(10.50m);
        updated.CategoryName.ShouldBe("Frames");
        updated.CreatedAt.ShouldBe(created.CreatedAt);
        updated.Version.ShouldNotBe(created.Version);
        (await Api.GetProductAsync(created.Id)).ShouldBe(updated);
    }

    [Fact]
    public async Task Update_ForUnknownProduct_ReturnsNotFound()
    {
        using var response = await Api.UpdateAsync(999_999, new UpdateProductRequest
        {
            Name = "x",
            Price = 1m,
            StockQuantity = 1,
            CategoryId = 1,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("W/\"1\"")]
    [InlineData("\"1\", \"2\"")]
    public async Task Update_WithMalformedIfMatch_ReturnsBadRequest(string ifMatch)
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var response = await Api.UpdateAsync(created.Id, TestProducts.UpdateRequest(created), ifMatch);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_RemovesTheProduct()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var delete = await Api.DeleteAsync(created.Id);
        using var get = await Api.GetAsync(created.Id);
        using var deleteAgain = await Api.DeleteAsync(created.Id);

        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deleteAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithStaleIfMatch_IsRejectedAndKeepsTheProduct()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 5));
        (await Api.DecrementStockAsync(created.Id, 1)).Dispose();

        using var response = await Api.DeleteAsync(created.Id, ifMatch: $"\"{created.Version}\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await Api.GetAsync(created.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
