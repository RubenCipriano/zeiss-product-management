using System.Net;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

public class ProductCrudTests(ApiFixture fixture) : ApiTestBase(fixture)
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
    public async Task Create_WithInvalidFields_ExplainsEachProblemInReadableWords()
    {
        using var response = await Api.CreateAsync(new { name = "", price = 10.999m, stockQuantity = -1, categoryId = 1 });

        var errors = (await ReadProblemAsync(response)).Errors!;
        errors["name"].ShouldBe(["'Name' must not be empty."]);
        errors["price"].ShouldBe(["'Price' must not have more than 2 decimal places."]);
        errors["stockQuantity"].ShouldBe(["'Stock Quantity' must be between 0 and 1000000. You entered -1."]);
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
    public async Task Create_WhenEveryIdentifierHasBeenAssigned_ReturnsServiceUnavailableAndCreatesNothing()
    {
        var sequence = await ReadProductIdSequenceAsync();
        await SetProductIdSequenceAsync(ProductRules.MaxId, isCalled: true);
        try
        {
            using var response = await Api.CreateAsync(TestProducts.CreateRequest());

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            response.ContentType()!.MediaType.ShouldBe("application/problem+json");
            (await ReadProblemAsync(response)).ErrorCode.ShouldBe("product.ids_exhausted");
            using var list = await Api.ListAsync();
            (await ReadPageAsync(list)).TotalCount.ShouldBe(0);
        }
        finally
        {
            await SetProductIdSequenceAsync(sequence.LastValue, sequence.IsCalled);
        }
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
    public async Task GetById_WithTheCurrentETagInIfNoneMatch_ReturnsNotModifiedUntilTheProductChanges()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        var etag = $"\"{created.Version}\"";

        using var unchanged = await GetIfNoneMatchAsync(created.Id, etag);
        (await Api.DecrementStockAsync(created.Id, 1)).Dispose();
        using var changed = await GetIfNoneMatchAsync(created.Id, etag);

        unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        unchanged.ETag().ShouldBe(etag);
        (await unchanged.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(changed)).StockQuantity.ShouldBe(9);
        changed.ETag().ShouldNotBe(etag);
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
    public async Task List_WithoutPaging_UsesDefaultPageSize20()
    {
        for (var i = 0; i < 21; i++)
        {
            await Api.CreateProductAsync(TestProducts.CreateRequest(name: $"Product {i:D2}"));
        }

        using var response = await Api.Http.GetAsync(new Uri("api/products", UriKind.Relative), CancellationToken);

        var page = await ReadPageAsync(response);
        page.Page.ShouldBe(1);
        page.PageSize.ShouldBe(20);
        page.Items.Count.ShouldBe(20);
        page.TotalCount.ShouldBe(21);
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

    [Fact]
    public async Task Update_WithInvalidFields_ReturnsProblemDetailsListingEveryInvalidField()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Unchanged"));

        using var response = await Api.UpdateAsync(created.Id, new { name = " ", price = 0m, stockQuantity = ProductRules.MaxStockQuantity + 1, categoryId = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ContentType()!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadProblemAsync(response);
        problem.ErrorCode.ShouldBe("validation_failed");
        problem.Errors!.Keys.ShouldBe(["name", "price", "stockQuantity"], ignoreOrder: true);
        (await Api.GetProductAsync(created.Id)).ShouldBe(created);
    }

    [Fact]
    public async Task Update_WithMissingFields_ReportsThem()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var response = await Api.UpdateAsync(created.Id, new { name = "Frame" });

        (await ReadProblemAsync(response)).Errors!.Keys.ShouldBe(["price", "stockQuantity", "categoryId"], ignoreOrder: true);
    }

    [Fact]
    public async Task Update_ToUnknownCategory_ReturnsValidationError()
    {
        var created = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var response = await Api.UpdateAsync(created.Id, TestProducts.UpdateRequest(created, categoryId: 999));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.ShouldContainKey("categoryId");
        (await Api.GetProductAsync(created.Id)).CategoryId.ShouldBe(created.CategoryId);
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
    public async Task Delete_ThenCreate_NeverReusesTheId()
    {
        var deleted = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Deleted"));
        (await Api.DeleteAsync(deleted.Id)).Dispose();

        var created = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Created after"));

        created.Id.ShouldBeGreaterThan(deleted.Id);
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

    private Task<HttpResponseMessage> GetIfNoneMatchAsync(int id, string ifNoneMatch)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri($"api/products/{id}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return Api.Http.SendAsync(message, CancellationToken);
    }

    // The sequence is not part of the data reset between tests, so a test that moves it puts it back.
    private async Task<(long LastValue, bool IsCalled)> ReadProductIdSequenceAsync()
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT last_value, is_called FROM product_id_seq", connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        await reader.ReadAsync(CancellationToken);
        return (reader.GetInt64(0), reader.GetBoolean(1));
    }

    private async Task SetProductIdSequenceAsync(long value, bool isCalled)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT setval('product_id_seq', @value, @isCalled)", connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("isCalled", isCalled);
        await command.ExecuteScalarAsync(CancellationToken);
    }
}
