using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ProductManagement.Core.Common.Errors;
using ProductManagement.Core.Common.Results;

namespace ProductManagement.Api.Http;

/// <summary>
/// Base class for the API controllers: turns a use case's <see cref="Result"/> into an HTTP response, so every
/// endpoint maps errors to status codes and to "problem details" error bodies (RFC 9457) in the same way.
/// </summary>
/// <remarks>
/// <c>[ApiController]</c> switches on ASP.NET Core's API conventions: the JSON body is bound automatically, and an
/// invalid request is answered with a 400 before the action runs.
/// </remarks>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    public const string ProblemJsonContentType = ApiProblems.ContentType;

    protected ActionResult<T> ToActionResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(result.Error);

    /// <summary>The error as an HTTP response: the status code comes from <see cref="Error.Type"/>.</summary>
    protected ObjectResult Problem(Error error)
    {
        if (error.Type == ErrorType.Validation)
        {
            return ValidationProblem(error);
        }

        var statusCode = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problem = ProblemDetailsFactory
            .CreateProblemDetails(HttpContext, statusCode, detail: error.Message)
            .WithErrorCode(error.Code);
        foreach (var (key, value) in error.Metadata)
        {
            problem.Extensions[key] = value;
        }

        return problem.ToResult();
    }

    protected ObjectResult InvalidIfMatchHeader() =>
        Problem(Error.Validation(
            "If-Match",
            "The If-Match header must be a single strong entity tag, e.g. \"123\", or *."));

    private ObjectResult ValidationProblem(Error error)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (field, messages) in error.ValidationErrors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(field, message);
            }
        }

        return ProblemDetailsFactory
            .CreateValidationProblemDetails(
                HttpContext,
                modelState,
                StatusCodes.Status400BadRequest,
                detail: error.Message)
            .WithErrorCode(error.Code)
            .ToResult();
    }

    /// <summary>A request refused before reaching a use case (see <see cref="ApiProblems.Rejection"/>).</summary>
    protected ObjectResult Rejection(int status, string errorCode, string detail) =>
        ProblemDetailsFactory.Rejection(HttpContext, status, errorCode, detail);
}
