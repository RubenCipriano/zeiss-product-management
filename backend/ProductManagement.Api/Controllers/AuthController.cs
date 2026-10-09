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
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController : ApiControllerBase
{
    /// <summary>Issues an access token for a registered API consumer (client credentials).</summary>
    [HttpPost("token")]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemJsonContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJsonContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemJsonContentType)]
    public ActionResult<TokenResponse> IssueToken(TokenRequest request, [FromServices] AccessTokenIssuer issuer)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrEmpty(request.ClientSecret))
        {
            return Problem(Error.Validation("clientId", "'clientId' and 'clientSecret' are required."));
        }

        var token = issuer.Authenticate(request.ClientId, request.ClientSecret);
        if (token is null)
        {
            // One message for unknown client and wrong secret: no hint about which client ids exist.
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext,
                StatusCodes.Status401Unauthorized,
                detail: "Invalid client credentials.");
            problem.Extensions["errorCode"] = "invalid_client";
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status401Unauthorized,
                ContentTypes = { ProblemJsonContentType },
            };
        }

        return Ok(new TokenResponse(token.AccessToken, "Bearer", token.ExpiresInSeconds));
    }
}
