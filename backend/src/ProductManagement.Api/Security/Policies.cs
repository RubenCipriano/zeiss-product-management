using Microsoft.AspNetCore.Authorization;

namespace ProductManagement.Api.Security;

/// <summary>
/// Authorization policies, named after <em>permissions</em>. Endpoints require a permission, never a role, so the
/// role → permission mapping lives in one place (<see cref="Configure"/>) and can change without touching
/// controllers.
/// </summary>
public static class Policies
{
    public const string ProductsRead = "products:read";

    public const string StockWrite = "stock:write";

    public const string ProductsWrite = "products:write";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(ProductsRead, policy => policy.RequireRole(Roles.Viewer, Roles.StockClerk, Roles.Manager));
        options.AddPolicy(StockWrite, policy => policy.RequireRole(Roles.StockClerk, Roles.Manager));
        options.AddPolicy(ProductsWrite, policy => policy.RequireRole(Roles.Manager));

        // Secure by default: an endpoint without an explicit policy still requires a signed-in user.
        // Anonymous endpoints (health, OpenAPI) opt out explicitly with AllowAnonymous().
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}
