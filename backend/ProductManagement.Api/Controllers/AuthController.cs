using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using ProductManagement.Api.Http;
using ProductManagement.Api.RateLimiting;
using ProductManagement.Api.Security;
using System.Globalization;
using ProductManagement.Application.Common.Errors;
using ProductManagement.Application.Common.Security;

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
public sealed class AuthController(AccessTokenIssuer issuer, IFailedAttemptThrottle failedAttempts) : ApiControllerBase
{
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

        var callerKey = $"ip:{HttpContext.Connection.RemoteIpAddress}";
        if (await failedAttempts.GetBlockedForAsync(callerKey, cancellationToken) is { } blockedFor)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(blockedFor.TotalSeconds));
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return ProblemResponse(
                StatusCodes.Status429TooManyRequests,
                "too_many_failed_attempts",
                $"Too many failed sign-in attempts. Try again in {retryAfter} second(s).");
        }

        var token = issuer.Authenticate(request.ClientId, request.ClientSecret);
        if (token is null)
        {
            await failedAttempts.RecordFailureAsync(callerKey, cancellationToken);

            // One message for unknown client and wrong secret: no hint about which client ids exist.
            return ProblemResponse(StatusCodes.Status401Unauthorized, "invalid_client", "Invalid client credentials.");
        }

        return Ok(new TokenResponse(token.AccessToken, "Bearer", token.ExpiresInSeconds));
    }

    private ObjectResult ProblemResponse(int status, string errorCode, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            status,
            title: ReasonPhrases.GetReasonPhrase(status),
            detail: detail);
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { ProblemJsonContentType } };
    }
}
