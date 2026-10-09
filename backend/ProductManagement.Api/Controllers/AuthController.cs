using System.Globalization;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProductManagement.Api.Http;
using ProductManagement.Api.RateLimiting;
using ProductManagement.Api.Security;
using ProductManagement.Application.Common.Errors;

namespace ProductManagement.Api.Controllers;

/// <summary>
/// Token endpoint: a registered consumer exchanges its client id and secret for a signed JWT.
/// </summary>
/// <remarks>
/// Two layers against secret guessing: a per-replica rate limit on the endpoint, and a cluster-wide count of failed
/// attempts per client IP (Redis) that blocks the IP for the rest of the window once it fails too often.
/// </remarks>
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(TokenService tokens, TokenSigningKey signingKey) : ApiControllerBase
{
    /// <summary>
    /// The public key(s) that verify access tokens (JWKS, RFC 7517). Lets the gateway and other services check tokens
    /// without being able to issue them. Public by nature: it contains no secret.
    /// </summary>
    [HttpGet("/.well-known/jwks.json")]
    [ProducesResponseType<JsonWebKeySet>(StatusCodes.Status200OK)]
    public ActionResult<JsonWebKeySet> GetSigningKeys()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return new JsonWebKeySet([signingKey.PublicJwk]);
    }

    /// <summary>Issues an access token for a registered API consumer (client credentials).</summary>
    [HttpPost("token")]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemJsonContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJsonContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemJsonContentType)]
    public async Task<ActionResult<TokenResponse>> IssueToken(
        TokenRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrEmpty(request.ClientSecret))
        {
            return Problem(Error.Validation("clientId", "'clientId' and 'clientSecret' are required."));
        }

        var outcome = await tokens.IssueAsync(
            request.ClientId,
            request.ClientSecret,
            HttpContext.GetClientIpKey(),
            cancellationToken);

        switch (outcome)
        {
            case TokenOutcome.Issued(var token):
                return Ok(new TokenResponse(token.AccessToken, "Bearer", token.ExpiresInSeconds));
            case TokenOutcome.Blocked(var blockedFor):
                var retryAfter = Math.Max(1, (int)Math.Ceiling(blockedFor.TotalSeconds));
                Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                return Rejection(
                    StatusCodes.Status429TooManyRequests,
                    ApiErrorCodes.TooManyFailedAttempts,
                    $"Too many failed sign-in attempts. Try again in {retryAfter} second(s).");
            default:
                // One message for unknown client and wrong secret: no hint about which client ids exist.
                return Rejection(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidClient, "Invalid client credentials.");
        }
    }
}
