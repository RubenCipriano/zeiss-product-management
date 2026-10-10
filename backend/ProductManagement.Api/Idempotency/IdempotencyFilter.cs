using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ProductManagement.Api.Http;
using ProductManagement.Api.Security;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Api.Idempotency;

/// <summary>
/// Makes retries safe on the endpoints marked <see cref="IdempotentAttribute"/>: a client may attach an
/// <c>Idempotency-Key</c> header (an id it chooses), and the same request sent twice with that key is applied once.
/// Runs around the controller action (an MVC filter).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>first request with a key: processed, and its response stored (24 h) in the shared store;</item>
///   <item>
///     same key again (any replica): the stored response is returned with <c>Idempotent-Replayed: true</c>, nothing
///     runs;
///   </item>
///   <item>same key while the first is still running: <c>409</c>; same key with a different body: <c>422</c>;</item>
///   <item>store unavailable: <c>503</c>, because exactly-once can no longer be guaranteed.</item>
/// </list>
/// Keys are scoped to the caller (<c>sub</c>), so consumers can never see or block each other's keys. The header is
/// optional: requests without it behave as before. See ADR 0017 (idempotency keys).
/// </remarks>
internal class IdempotencyFilter(IIdempotencyStore store, ProblemDetailsFactory problemDetailsFactory)
    : IAsyncResourceFilter
{
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    // Headers that are part of the response a client relies on, replayed with the body.
    private static readonly string[] ReplayedHeaders = ["ETag", "Location"];

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Headers.TryGetValue(IdempotencyKey.HeaderName, out var values))
        {
            await next();
            return;
        }

        if (!IdempotencyKey.TryParse(values.ToString(), http.GetSubject() ?? "anonymous", out var scopedKey))
        {
            context.Result = Reject(
                http,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.IdempotencyKeyInvalid,
                IdempotencyKey.Rule);
            return;
        }

        // Also recorded with the change in the database (unique), the backstop if this claim is ever lost.
        HttpRequestContext.SetIdempotencyKey(http, scopedKey);
        var fingerprint = await RequestFingerprint.ComputeAsync(http.Request);
        var claim = await store.TryBeginAsync(scopedKey, fingerprint, http.RequestAborted);

        if (AnswerWithoutRunning(http, claim) is { } answer)
        {
            context.Result = answer;
            return;
        }

        await ExecuteAndRememberAsync(http, next, scopedKey, fingerprint);
    }

    /// <summary>
    /// The response for a key the store already knows (replay, or a 409/422/503 refusal); <c>null</c> when this
    /// request claimed the key and must run.
    /// </summary>
    private IActionResult? AnswerWithoutRunning(HttpContext http, IdempotencyClaim claim)
    {
        switch (claim.Status)
        {
            case IdempotencyClaimStatus.Completed:
                return Replay(http, claim.Response!);

            case IdempotencyClaimStatus.InProgress:
                http.Response.Headers.RetryAfter = "1";
                return Reject(
                    http,
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyInProgress,
                    "A request with this Idempotency-Key is still being processed. Retry shortly.");

            case IdempotencyClaimStatus.FingerprintMismatch:
                return Reject(
                    http,
                    StatusCodes.Status422UnprocessableEntity,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "This Idempotency-Key was already used for a different request.");

            case IdempotencyClaimStatus.Unavailable:
                return Reject(
                    http,
                    StatusCodes.Status503ServiceUnavailable,
                    ApiErrorCodes.IdempotencyUnavailable,
                    "The request cannot be guaranteed to run only once right now. Retry later with the same key.");

            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the action with the response body captured in memory, sends it to the client, then stores it under the
    /// key so a retry gets the same answer.
    /// </summary>
    private async Task ExecuteAndRememberAsync(
        HttpContext http,
        ResourceExecutionDelegate next,
        string scopedKey,
        string fingerprint)
    {
        var originalBody = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;

        ResourceExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch
        {
            http.Response.Body = originalBody;
            await store.AbandonAsync(scopedKey, CancellationToken.None);
            throw;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, http.RequestAborted);
        http.Response.Body = originalBody;

        // Server errors are not remembered: the client should be able to retry them with the same key.
        if ((executed.Exception is not null && !executed.ExceptionHandled)
            || http.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await store.AbandonAsync(scopedKey, CancellationToken.None);
            return;
        }

        var headers = ReplayedHeaders
            .Where(name => http.Response.Headers.ContainsKey(name))
            .ToDictionary(name => name, name => http.Response.Headers[name].ToString());
        var response = new StoredResponse(
            http.Response.StatusCode,
            http.Response.ContentType,
            Encoding.UTF8.GetString(buffer.ToArray()),
            headers);
        await store.CompleteAsync(scopedKey, fingerprint, response, CancellationToken.None);
    }

    private static ContentResult Replay(HttpContext http, StoredResponse response)
    {
        foreach (var (name, value) in response.Headers)
        {
            http.Response.Headers[name] = value;
        }

        http.Response.Headers[ReplayedHeaderName] = "true";
        return new ContentResult
        {
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Content = response.Body,
        };
    }

    private ObjectResult Reject(HttpContext http, int status, string errorCode, string detail) =>
        problemDetailsFactory.Rejection(http, status, errorCode, detail);
}
