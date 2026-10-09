using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProductManagement.Mcp.Api;

/// <summary>Typed client for the subset of the REST API the MCP tools use.</summary>
internal sealed class ProductApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ProductPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        GetAsync<ProductPage>(Invariant($"api/products?page={page}&pageSize={pageSize}"), cancellationToken);

    public Task<ProductPage> SearchAsync(string name, int page, int pageSize, CancellationToken cancellationToken) =>
        GetAsync<ProductPage>(
            Invariant($"api/products/search?name={Uri.EscapeDataString(name)}&page={page}&pageSize={pageSize}"),
            cancellationToken);

    public Task<ProductPage> ByStockLevelAsync(int? min, int? max, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Invariant($"api/products/stock-level?page={page}&pageSize={pageSize}");
        if (min is not null)
        {
            query += Invariant($"&min={min}");
        }

        if (max is not null)
        {
            query += Invariant($"&max={max}");
        }

        return GetAsync<ProductPage>(query, cancellationToken);
    }

    public Task<Product> GetAsync(int id, CancellationToken cancellationToken) =>
        GetAsync<Product>(Invariant($"api/products/{id}"), cancellationToken);

    public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<Category>>("api/categories", cancellationToken);

    public async Task<Product> AdjustStockAsync(int id, int quantity, bool add, CancellationToken cancellationToken)
    {
        var action = add ? "add-to-stock" : "decrement-stock";
        using var response = await http.PostAsync(
            new Uri(Invariant($"api/products/{id}/{action}/{quantity}"), UriKind.Relative),
            content: null,
            cancellationToken);
        return await ReadAsync<Product>(response, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(relativeUrl, UriKind.Relative), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new ProductApiException((int)response.StatusCode, null, "The API returned an empty response.");
        }

        ApiProblem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            // Not a problem-details body; fall back to the status code below.
        }

        throw new ProductApiException(
            (int)response.StatusCode,
            problem?.ErrorCode,
            problem?.Detail ?? problem?.Title ?? $"The API answered {(int)response.StatusCode}.");
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
