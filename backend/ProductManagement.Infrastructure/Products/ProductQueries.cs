using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

internal sealed class ProductQueries(ProductManagementDbContext context) : IProductQueries
{
    private const string LikeEscapeCharacter = "\\";

    /// <summary>
    /// The single mapping from entity to response. As an expression it is translated to SQL, so only the needed
    /// columns are read (with a join for the category name) and no entity is ever materialized or tracked.
    /// </summary>
    private static readonly Expression<Func<Product, ProductResponse>> ToResponse = product => new ProductResponse(
        product.Id,
        product.Name,
        product.Description,
        product.Price,
        product.StockQuantity,
        product.CategoryId,
        product.Category!.Name,
        product.CreatedAt,
        product.UpdatedAt,
        product.Version);

    private IQueryable<Product> Products => context.Products.AsNoTracking();

    public Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        Products
            .Where(product => product.Id == id)
            .Select(ToResponse)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProductResponse?> FindCreatedByRequestAsync(string requestKey, CancellationToken cancellationToken)
    {
        var productId = await context.StockMovements
            .Where(movement => movement.RequestKey == requestKey && movement.Reason == StockMovementReason.InitialStock)
            .Select(movement => (int?)movement.ProductId)
            .SingleOrDefaultAsync(cancellationToken);
        return productId is null ? null : await GetByIdAsync(productId.Value, cancellationToken);
    }

    public async Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken) =>
        await LoadPageAsync(await FindListPageAsync(page, cancellationToken), page, cancellationToken);

    public async Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        await LoadPageAsync(await FindSearchPageAsync(nameFragment, page, cancellationToken), page, cancellationToken);

    internal Task<ProductPageIds> FindListPageAsync(PageQuery page, CancellationToken cancellationToken) =>
        FindPageAsync(Products, ByName, page, cancellationToken);

    internal Task<int> CountListAsync(CancellationToken cancellationToken) => Products.CountAsync(cancellationToken);

    internal Task<int[]> FindListPageIdsAsync(PageQuery page, CancellationToken cancellationToken) =>
        PageIdsAsync(Products, ByName, page, cancellationToken);

    internal Task<ProductPageIds> FindSearchPageAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        FindPageAsync(NameContains(nameFragment), ByName, page, cancellationToken);

    internal Task<int> CountSearchAsync(string nameFragment, CancellationToken cancellationToken) =>
        NameContains(nameFragment).CountAsync(cancellationToken);

    internal Task<int[]> FindSearchPageIdsAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        PageIdsAsync(NameContains(nameFragment), ByName, page, cancellationToken);

    private IQueryable<Product> NameContains(string nameFragment)
    {
        // Wildcards typed by the user are matched literally: searching "50%" finds "50% off", not everything.
        var pattern = $"%{EscapeLikePattern(nameFragment)}%";
        return Products.Where(product => EF.Functions.ILike(product.Name, pattern, LikeEscapeCharacter));
    }

    /// <summary>Reads the current rows of a page found by one of the Find…PageAsync methods, in the page's order.</summary>
    internal async Task<PagedResponse<ProductResponse>> LoadPageAsync(
        ProductPageIds pageIds,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var ids = pageIds.Ids;
        if (ids.Count == 0)
        {
            return new PagedResponse<ProductResponse>([], page.Page, page.PageSize, pageIds.TotalCount);
        }

        var rows = await Products
            .Where(product => ids.Contains(product.Id))
            .Select(ToResponse)
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        // A product deleted since the page was found is simply missing from it.
        var items = ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        return new PagedResponse<ProductResponse>(items, page.Page, page.PageSize, pageIds.TotalCount);
    }

    public async Task<PagedResponse<StockMovementResponse>?> ListStockMovementsAsync(
        int productId,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        if (!await context.Products.AnyAsync(product => product.Id == productId, cancellationToken))
        {
            return null;
        }

        var history = context.StockMovements
            .AsNoTracking()
            .Where(movement => movement.ProductId == productId);

        var totalCount = await history.CountAsync(cancellationToken);
        var items = await history
            .OrderByDescending(movement => movement.OccurredAt)
            .ThenByDescending(movement => movement.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(movement => new StockMovementResponse(
                movement.Id,
                movement.QuantityChange,
                movement.StockAfter,
                movement.Reason.ToString(),
                movement.PerformedBy,
                movement.OccurredAt))
            .ToListAsync(cancellationToken);

        return new PagedResponse<StockMovementResponse>(items, page.Page, page.PageSize, totalCount);
    }

    public async Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var query = Products;
        if (min is not null)
        {
            query = query.Where(product => product.StockQuantity >= min);
        }

        if (max is not null)
        {
            query = query.Where(product => product.StockQuantity <= max);
        }

        var pageIds = await FindPageAsync(
            query,
            products => products
                .OrderBy(product => product.StockQuantity)
                .ThenBy(product => product.Name)
                .ThenBy(product => product.Id),
            page,
            cancellationToken);
        return await LoadPageAsync(pageIds, page, cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);

    private static IOrderedQueryable<Product> ByName(IQueryable<Product> products) =>
        products.OrderBy(product => product.Name).ThenBy(product => product.Id);

    /// <summary>
    /// Finds a page in two narrow queries: the count, and the page's ids. The rows themselves are read afterwards by
    /// primary key (<see cref="LoadPageAsync"/>). This is a late row lookup: skipping rows for a deep page walks the
    /// (name, id) index without touching the table, so page 40,000 of 850,000 products takes ~0.5 s instead of
    /// ~1.3 s. It also lets the cache keep this expensive part across stock changes, which only affect the rows
    /// (see <see cref="Caching.CachingProductQueries"/>). Offset paging stays linear in the page number; keyset
    /// paging would make it constant.
    /// </summary>
    private static async Task<ProductPageIds> FindPageAsync(
        IQueryable<Product> filtered,
        Func<IQueryable<Product>, IOrderedQueryable<Product>> order,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var totalCount = await filtered.CountAsync(cancellationToken);
        int[] ids = totalCount <= page.Skip ? [] : await PageIdsAsync(filtered, order, page, cancellationToken);
        return new ProductPageIds(ids, totalCount);
    }

    private static Task<int[]> PageIdsAsync(
        IQueryable<Product> filtered,
        Func<IQueryable<Product>, IOrderedQueryable<Product>> order,
        PageQuery page,
        CancellationToken cancellationToken) =>
        order(filtered)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(product => product.Id)
            .ToArrayAsync(cancellationToken);
}

/// <summary>The ids on one page of a list, in order, and the total number of matching products.</summary>
internal sealed record ProductPageIds(IReadOnlyList<int> Ids, int TotalCount);
