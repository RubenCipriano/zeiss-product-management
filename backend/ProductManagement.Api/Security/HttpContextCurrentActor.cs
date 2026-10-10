using ProductManagement.Core.Common.Security;

namespace ProductManagement.Api.Security;

/// <summary>The authenticated API consumer (<c>sub</c> claim) of the current request.</summary>
internal class HttpContextCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public string Name => httpContextAccessor.HttpContext?.GetSubject() ?? "system";
}
