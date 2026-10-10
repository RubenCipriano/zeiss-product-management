using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Testing;

/// <summary>
/// A thin, typed wrapper over the HTTP API. It goes through real HTTP (routing, model binding, serialization,
/// filters), so tests exercise exactly what a client would.
/// </summary>
public class ProductApiClient(HttpClient httpClient) : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Http => httpClient;

    public Task<HttpResponseMessage> CreateAsync(object request) =>
        httpClient.PostAsJsonAsync("api/products", request, JsonOptions);

    public Task<HttpResponseMessage> GetAsync(int id) => httpClient.GetAsync(new Uri($"api/products/{id}", UriKind.Relative));

    public Task<HttpResponseMessage> ListAsync(int page = 1, int pageSize = 20) =>
        httpClient.GetAsync(new Uri($"api/products?page={page}&pageSize={pageSize}", UriKind.Relative));

    public Task<HttpResponseMessage> SearchAsync(string? name, int pageSize = 20) =>
        httpClient.GetAsync(new Uri(
            $"api/products/search?pageSize={pageSize}" + (name is null ? string.Empty : $"&name={Uri.EscapeDataString(name)}"),
            UriKind.Relative));

    public Task<HttpResponseMessage> ByStockLevelAsync(int? min, int? max, int pageSize = 20)
    {
        var query = $"api/products/stock-level?pageSize={pageSize}";
        if (min is not null)
        {
            query += $"&min={min}";
        }

        if (max is not null)
        {
            query += $"&max={max}";
        }

        return httpClient.GetAsync(new Uri(query, UriKind.Relative));
    }

    public Task<HttpResponseMessage> UpdateAsync(int id, object request, string? ifMatch = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, new Uri($"api/products/{id}", UriKind.Relative))
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        AddIfMatch(message, ifMatch);
        return httpClient.SendAsync(message);
    }

    public Task<HttpResponseMessage> DeleteAsync(int id, string? ifMatch = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Delete, new Uri($"api/products/{id}", UriKind.Relative));
        AddIfMatch(message, ifMatch);
        return httpClient.SendAsync(message);
    }

    public Task<HttpResponseMessage> DecrementStockAsync(int id, int quantity) =>
        httpClient.PostAsync(new Uri($"api/products/{id}/decrement-stock/{quantity}", UriKind.Relative), content: null);

    public Task<HttpResponseMessage> AddToStockAsync(int id, int quantity) =>
        httpClient.PostAsync(new Uri($"api/products/{id}/add-to-stock/{quantity}", UriKind.Relative), content: null);

    /// <summary>Creates a product and fails fast if the API does not answer 201.</summary>
    public async Task<ProductResponse> CreateProductAsync(CreateProductRequest request)
    {
        using var response = await CreateAsync(request);
        await EnsureStatusAsync(response, HttpStatusCode.Created);
        return await ReadAsync<ProductResponse>(response);
    }

    public async Task<ProductResponse> GetProductAsync(int id)
    {
        using var response = await GetAsync(id);
        await EnsureStatusAsync(response, HttpStatusCode.OK);
        return await ReadAsync<ProductResponse>(response);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException($"Response body could not be read as {typeof(T).Name}.");

    public static Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response) => ReadAsync<ApiProblem>(response);

    public static Task<PagedResponse<ProductResponse>> ReadPageAsync(HttpResponseMessage response) =>
        ReadAsync<PagedResponse<ProductResponse>>(response);

    public void Dispose() => httpClient.Dispose();

    private static async Task EnsureStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        }
    }

    private static void AddIfMatch(HttpRequestMessage message, string? ifMatch)
    {
        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
    }
}

public static class HttpResponseMessageExtensions
{
    public static string? ETag(this HttpResponseMessage response) => response.Headers.ETag?.Tag;

    public static MediaTypeHeaderValue? ContentType(this HttpResponseMessage response) => response.Content.Headers.ContentType;
}
