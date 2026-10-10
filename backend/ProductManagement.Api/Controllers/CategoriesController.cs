using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProductManagement.Api.Http;
using ProductManagement.Api.Security;
using ProductManagement.Core.Categories;
using ProductManagement.Core.Categories.Contracts;

namespace ProductManagement.Api.Controllers;

/// <summary>
/// <c>GET /api/categories</c>: the product categories (fixed reference data, read-only).
/// </summary>
[Route("api/categories")]
[Authorize(Policy = Policies.ProductsRead)]
public class CategoriesController(ICategoryQueryService categoryQueries) : ApiControllerBase
{
    /// <summary>Lists all categories, ordered by name.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetCategories(
        CancellationToken cancellationToken) =>
        ToActionResult(await categoryQueries.ListAsync(cancellationToken));
}
