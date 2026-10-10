using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace ProductManagement.Api.Http;

/// <summary>
/// Builds the error responses of the API. Every error is a "problem details" JSON object (RFC 9457, the standard
/// shape for HTTP errors; ADR 0005): the standard members from ASP.NET Core's
/// <see cref="ProblemDetailsFactory"/> or <see cref="IProblemDetailsService"/> (<c>type</c>, <c>title</c>,
/// <c>status</c>, <c>detail</c>, <c>traceId</c>), plus a machine-readable <c>errorCode</c> (<see cref="ApiErrorCodes"/>
/// at the HTTP edge, the error's own code for use-case results).
/// </summary>
internal static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    /// <summary>The extension member that carries the machine-readable code.</summary>
    public const string ErrorCodeKey = "errorCode";

    public static TProblem WithErrorCode<TProblem>(this TProblem problem, string errorCode)
        where TProblem : ProblemDetails
    {
        problem.Extensions[ErrorCodeKey] = errorCode;
        return problem;
    }

    /// <summary>
    /// A problem as an MVC result: <c>application/problem+json</c>, or plain JSON for a client that asks for it.
    /// </summary>
    public static ObjectResult ToResult(this ProblemDetails problem) =>
        new(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { ContentType, MediaTypeNames.Application.Json },
        };

    /// <summary>
    /// A request refused at the HTTP edge (before or instead of a use case), titled with the status's reason phrase.
    /// Always <c>application/problem+json</c>.
    /// </summary>
    public static ObjectResult Rejection(
        this ProblemDetailsFactory factory,
        HttpContext httpContext,
        int status,
        string errorCode,
        string detail)
    {
        var problem = factory
            .CreateProblemDetails(httpContext, status, title: ReasonPhrases.GetReasonPhrase(status), detail: detail)
            .WithErrorCode(errorCode);
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { ContentType } };
    }
}
