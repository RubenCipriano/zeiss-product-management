using ProductManagement.Application.Common.Security;

namespace ProductManagement.Api.Security;

/// <summary>The authenticated API consumer (<c>sub</c> claim) of the current request.</summary>
internal sealed class HttpContextCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public string Name => httpContextAccessor.HttpContext?.GetSubject() ?? "system";
}
