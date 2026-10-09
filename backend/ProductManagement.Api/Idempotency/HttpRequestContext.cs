using ProductManagement.Application.Common.Requests;

namespace ProductManagement.Api.Idempotency;

/// <summary>Exposes the current request's idempotency key, as accepted by <see cref="IdempotencyFilter"/>.</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    private const string IdempotencyKeyItem = "IdempotencyRequestKey";

    public string? IdempotencyKey =>
        httpContextAccessor.HttpContext?.Items.TryGetValue(IdempotencyKeyItem, out var key) == true
            ? key as string
            : null;

    public static void SetIdempotencyKey(HttpContext httpContext, string scopedKey) =>
        httpContext.Items[IdempotencyKeyItem] = scopedKey;
}
