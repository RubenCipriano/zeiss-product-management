using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// The product read queries, uncached. <see cref="Caching.CachingProductQueries"/>, the registered
/// <see cref="IProductQueries"/>, composes them with the cache (which, without Redis, is a pass-through).
/// </summary>
internal sealed class ProductQueries(ProductManagementDbContext context)
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

    /// <summary>
    /// One page of a listing, found in two narrow queries (count, then ids) and then read by primary key. This is a late
    /// row lookup: skipping rows for a deep page walks the (name, id) index without touching the table, so page 40,000
    /// of 850,000 products takes ~0.5 s instead of ~1.3 s. It also lets the cache keep the expensive part (count and
    /// ids) across stock changes, which only affect the rows (see <see cref="Caching.CachingProductQueries"/>). Offset
    /// paging stays linear in the page number; keyset paging would make it constant.
    /// </summary>
    /// <param name="count">The total number of matching products (by default <see cref="CountAsync"/>).</param>
    /// <param name="pageIds">The page's ids, in order (by default <see cref="PageIdsAsync"/>).</param>
    public async Task<PagedResponse<ProductResponse>> PageAsync(
        PageQuery page,
        Func<Task<int>> count,
        Func<Task<int[]>> pageIds,
        CancellationToken cancellationToken)
    {
        var totalCount = await count();
        int[] ids = totalCount <= page.Skip ? [] : await pageIds();
        if (ids.Length == 0)
        {
            return new PagedResponse<ProductResponse>([], page.Page, page.PageSize, totalCount);
        }

        var rows = await Products
            .Where(product => ids.Contains(product.Id))
            .Select(ToResponse)
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        // A product deleted since the page was found is simply missing from it.
        var items = ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        return new PagedResponse<ProductResponse>(items, page.Page, page.PageSize, totalCount);
    }

    /// <summary>One page of <paramref name="listing"/>, straight from the database.</summary>
    public Task<PagedResponse<ProductResponse>> PageAsync(
        ProductListing listing,
        PageQuery page,
        CancellationToken cancellationToken) =>
        PageAsync(
            page,
            () => CountAsync(listing, cancellationToken),
            () => PageIdsAsync(listing, page, cancellationToken),
            cancellationToken);

    public Task<int> CountAsync(ProductListing listing, CancellationToken cancellationToken) =>
        Filter(listing).CountAsync(cancellationToken);

    public Task<int[]> PageIdsAsync(ProductListing listing, PageQuery page, CancellationToken cancellationToken) =>
        Order(listing, Filter(listing))
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(product => product.Id)
            .ToArrayAsync(cancellationToken);

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

    private IQueryable<Product> Filter(ProductListing listing) =>
        listing switch
        {
            ProductListing.NameContains(var fragment) => NameContains(fragment),
            ProductListing.StockBetween(var min, var max) => StockBetween(min, max),
            _ => Products,
        };

    private IQueryable<Product> NameContains(string fragment)
    {
        // Wildcards typed by the user are matched literally: searching "50%" finds "50% off", not everything.
        var pattern = $"%{EscapeLikePattern(fragment)}%";
        return Products.Where(product => EF.Functions.ILike(product.Name, pattern, LikeEscapeCharacter));
    }

    private IQueryable<Product> StockBetween(int? min, int? max)
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

        return query;
    }

    private static IOrderedQueryable<Product> Order(ProductListing listing, IQueryable<Product> products) =>
        listing is ProductListing.StockBetween
            ? products.OrderBy(product => product.StockQuantity).ThenBy(product => product.Name).ThenBy(product => product.Id)
            : products.OrderBy(product => product.Name).ThenBy(product => product.Id);

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);
}
