using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProductManagement.Api.Http;
using ProductManagement.Api.Security;
using ProductManagement.Application.Categories;
using ProductManagement.Application.Categories.Contracts;

namespace ProductManagement.Api.Controllers;

/// <summary>
/// Product categories (read-only reference data).
/// </summary>
[Route("api/categories")]
[Authorize(Policy = Policies.ProductsRead)]
public sealed class CategoriesController(ICategoryQueryService categoryQueries) : ApiControllerBase
{
    /// <summary>Lists all categories, ordered by name.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetCategories(CancellationToken cancellationToken) =>
        Ok(await categoryQueries.ListAsync(cancellationToken));
}
