using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ProductManagement.Api.Http;

namespace ProductManagement.Api.ErrorHandling;

/// <summary>
/// Last line of defence for exceptions nothing else handled: logs them and returns an RFC 9457 "problem details"
/// error response (the standard JSON shape for HTTP errors) that does not reveal internal details.
/// </summary>
/// <remarks>
/// Expected failures (validation, not found, conflicts) never get here: the use cases return them as
/// results. Anything that reaches this handler is a bug or an infrastructure fault.
/// </remarks>
internal partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard status (popularised by nginx) for "the client closed the connection first".</summary>
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing to respond to and nothing worth an error log.
            LogRequestAborted(httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var (statusCode, title) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode, "The request could not be processed."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        var problem = new ProblemDetails { Status = statusCode, Title = title };
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(exception, httpContext.Request.Method, httpContext.Request.Path);
            problem.WithErrorCode(ApiErrorCodes.InternalError);
        }

        httpContext.Response.StatusCode = statusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while processing {Method} {Path}")]
    private partial void LogUnhandledException(Exception exception, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request {Method} {Path} was aborted by the client")]
    private partial void LogRequestAborted(string method, PathString path);
}
