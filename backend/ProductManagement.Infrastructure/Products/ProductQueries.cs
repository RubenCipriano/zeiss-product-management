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

    public Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken) =>
        ToPageAsync(
            Products.OrderBy(product => product.Name).ThenBy(product => product.Id),
            page,
            cancellationToken);

    public Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        // Wildcards typed by the user are matched literally: searching "50%" finds "50% off", not everything.
        var pattern = $"%{EscapeLikePattern(nameFragment)}%";

        return ToPageAsync(
            Products
                .Where(product => EF.Functions.ILike(product.Name, pattern, LikeEscapeCharacter))
                .OrderBy(product => product.Name)
                .ThenBy(product => product.Id),
            page,
            cancellationToken);
    }

    public Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
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

        return ToPageAsync(
            query
                .OrderBy(product => product.StockQuantity)
                .ThenBy(product => product.Name)
                .ThenBy(product => product.Id),
            page,
            cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);

    private static async Task<PagedResponse<ProductResponse>> ToPageAsync(
        IOrderedQueryable<Product> query,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(ToResponse)
            .ToListAsync(cancellationToken);

        return new PagedResponse<ProductResponse>(items, page.Page, page.PageSize, totalCount);
    }
}
