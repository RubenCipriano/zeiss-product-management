using System.Net;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

/// <summary>
/// The stock ledger: every change is recorded atomically with the change itself, so for every product the sum of its
/// movements equals its stock, even under concurrency.
/// </summary>
public class StockLedgerTests(ApiFixture fixture) : ApiTestBase(fixture)
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
    public async Task HistoryPages_CoverTheWholeLedger_NewestFirst_WithoutGapsOrRepeats()
    {
        // 1 initial movement + 44 changes = 45 movements: pages of 20, 20 and 5.
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 100));
        for (var change = 1; change <= 44; change++)
        {
            using var response = change % 2 == 0
                ? await Api.AddToStockAsync(product.Id, change)
                : await Api.DecrementStockAsync(product.Id, 1);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var pages = new[]
        {
            await HistoryAsync(product.Id, page: 1, pageSize: 20),
            await HistoryAsync(product.Id, page: 2, pageSize: 20),
            await HistoryAsync(product.Id, page: 3, pageSize: 20),
        };
        var beyondTheEnd = await HistoryAsync(product.Id, page: 4, pageSize: 20);

        pages.Select(p => p.Items.Count).ShouldBe([20, 20, 5]);
        pages.ShouldAllBe(p => p.TotalCount == 45 && p.TotalPages == 3);
        beyondTheEnd.Items.ShouldBeEmpty();
        beyondTheEnd.TotalCount.ShouldBe(45);

        // Read back to front (oldest first), the pages are the ledger itself: each stock_after is the running total.
        var ledger = pages.SelectMany(p => p.Items).Reverse().ToList();
        ledger.Select(m => m.Id).ShouldBeUnique();
        ledger[0].Reason.ShouldBe("InitialStock");
        var running = 0;
        foreach (var movement in ledger)
        {
            running += movement.QuantityChange;
            movement.StockAfter.ShouldBe(running);
        }

        ledger.Zip(ledger.Skip(1)).ShouldAllBe(pair =>
            pair.First.OccurredAt < pair.Second.OccurredAt
            || (pair.First.OccurredAt == pair.Second.OccurredAt && pair.First.Id < pair.Second.Id));
        running.ShouldBe((await Api.GetProductAsync(product.Id)).StockQuantity);
    }

    [Fact]
    public async Task HistoryPages_FollowTheTimeOfTheChange_EvenWhenIdsDisagree()
    {
        // occurred_at is the order of the history; ids normally agree with it, but the page must not depend on that.
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 5));
        await ExecuteAsync(
            """
            INSERT INTO stock_movements (product_id, quantity_change, stock_after, reason, performed_by, occurred_at)
            SELECT @id, 0, 5, 'Correction', 'backdated-' || n, now() - n * interval '1 day'
            FROM generate_series(1, 3) AS n
            """,
            product.Id);

        var first = await HistoryAsync(product.Id, page: 1, pageSize: 2);
        var second = await HistoryAsync(product.Id, page: 2, pageSize: 2);

        first.Items.Select(m => m.PerformedBy).ShouldBe(["manager-user", "backdated-1"]);
        second.Items.Select(m => m.PerformedBy).ShouldBe(["backdated-2", "backdated-3"]);
    }

    [Fact]
    public async Task HistoryOfAnUnknownProduct_IsNotFound()
    {
        using var response = await Api.Http.GetAsync(new Uri("api/products/999999/stock-movements", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PagedResponse<StockMovementResponse>> HistoryAsync(int productId, int page = 1, int pageSize = 20)
    {
        using var response = await Api.Http.GetAsync(
            new Uri($"api/products/{productId}/stock-movements?page={page}&pageSize={pageSize}", UriKind.Relative),
            CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReadAsync<PagedResponse<StockMovementResponse>>(response);
    }

    private async Task ExecuteAsync(string sql, int productId)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", productId);
        await command.ExecuteNonQueryAsync(CancellationToken);
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
