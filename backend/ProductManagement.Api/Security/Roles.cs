namespace ProductManagement.Api.Security;

/// <summary>
/// Roles of the registered API consumers, carried in the access token's <c>roles</c> claim.
/// </summary>
public static class Roles
{
    /// <summary>Read-only access to the catalogue.</summary>
    public const string Viewer = "viewer";

    /// <summary>Can also record stock movements (deliveries and sales).</summary>
    public const string StockClerk = "stock-clerk";

    /// <summary>Full access: can also create, edit and delete products.</summary>
    public const string Manager = "manager";
}
