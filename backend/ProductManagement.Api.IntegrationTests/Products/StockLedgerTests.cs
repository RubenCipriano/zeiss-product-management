using System.Net;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

/// <summary>
/// The stock ledger: every change is recorded atomically with the change itself, so for every product the sum of its
/// movements equals its stock, even under concurrency.
/// </summary>
public sealed class StockLedgerTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task EveryKindOfChange_IsRecorded_AndTheLedgerAddsUpToTheStock()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        (await Api.DecrementStockAsync(product.Id, 3)).Dispose();
        (await Api.AddToStockAsync(product.Id, 5)).Dispose();
        var current = await Api.GetProductAsync(product.Id);
        (await Api.UpdateAsync(product.Id, TestProducts.UpdateRequest(current, stockQuantity: 20))).Dispose();

        var history = await HistoryAsync(product.Id);

        history.Items.Select(m => (m.Reason, m.QuantityChange, m.StockAfter)).ShouldBe(
        [
            ("Correction", 8, 20),
            ("StockAdded", 5, 12),
            ("StockRemoved", -3, 7),
            ("InitialStock", 10, 10),
        ]);
        history.Items.Sum(m => m.QuantityChange).ShouldBe((await Api.GetProductAsync(product.Id)).StockQuantity);
        history.Items.ShouldAllBe(m => m.PerformedBy == "manager-user");
    }

    [Fact]
    public async Task RejectedChanges_LeaveNoTrace()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 2));

        (await Api.DecrementStockAsync(product.Id, 3)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await HistoryAsync(product.Id)).Items.Single().Reason.ShouldBe("InitialStock");
    }

    [Fact]
    public async Task UnderConcurrentOrders_OneMovementPerSuccessfulOrder()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 20));

        var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            using var response = await Api.DecrementStockAsync(product.Id, 1);
            return response.StatusCode;
        }));

        statuses.Count(s => s == HttpStatusCode.OK).ShouldBe(20);
        var removals = await CountRowsAsync(
            "SELECT count(*) FROM stock_movements WHERE product_id = @id AND reason = 'StockRemoved'", product.Id);
        var sum = await CountRowsAsync(
            "SELECT coalesce(sum(quantity_change), 0) FROM stock_movements WHERE product_id = @id", product.Id);
        removals.ShouldBe(20);
        sum.ShouldBe(0);
    }

    [Fact]
    public async Task HistoryOfAnUnknownProduct_IsNotFound()
    {
        using var response = await Api.Http.GetAsync(new Uri("api/products/999999/stock-movements", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PagedResponse<StockMovementResponse>> HistoryAsync(int productId)
    {
        using var response = await Api.Http.GetAsync(
            new Uri($"api/products/{productId}/stock-movements", UriKind.Relative),
            CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReadAsync<PagedResponse<StockMovementResponse>>(response);
    }

    private async Task<long> CountRowsAsync(string sql, int productId)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", productId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }
}
