using System.Security.Claims;

namespace ProductManagement.Api.Security;

/// <summary>Who is calling, in the forms the API keys its per-caller state by.</summary>
internal static class HttpContextCallerExtensions
{
    /// <summary>The signed-in consumer's client id, or <c>null</c> for an anonymous request.</summary>
    public static string? GetSubject(this HttpContext httpContext) =>
        httpContext.User.FindFirstValue(TokenClaims.Subject);

    /// <summary>
    /// The client address as a throttling key. Behind the gateway it is the forwarded client address (see
    /// <c>ForwardedHeadersOptions</c> in <c>AddApiPresentation</c>).
    /// </summary>
    public static string GetClientIpKey(this HttpContext httpContext) =>
        $"ip:{httpContext.Connection.RemoteIpAddress}";
}
