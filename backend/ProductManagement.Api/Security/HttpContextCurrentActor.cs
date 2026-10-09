using ProductManagement.Api.Idempotency;
using ProductManagement.Application.Common.Security;

namespace ProductManagement.Api.Security;

/// <summary>The authenticated API consumer (<c>sub</c> claim) of the current request.</summary>
internal sealed class HttpContextCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public string Name => httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value ?? "system";

    public string? RequestKey =>
        httpContextAccessor.HttpContext?.Items.TryGetValue(IdempotencyFilter.RequestKeyItem, out var key) == true
            ? key as string
            : null;
}
