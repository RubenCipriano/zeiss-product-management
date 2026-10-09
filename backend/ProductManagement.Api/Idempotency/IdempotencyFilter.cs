using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using ProductManagement.Api.Http;
using ProductManagement.Application.Common.Idempotency;

namespace ProductManagement.Api.Idempotency;

/// <summary>
/// Implements <c>Idempotency-Key</c> for the endpoints marked <see cref="IdempotentAttribute"/>:
/// <list type="bullet">
///   <item>first request with a key: processed, and its response stored (24 h) in the shared store;</item>
///   <item>same key again (any replica): the stored response is returned with <c>Idempotent-Replayed: true</c>, nothing runs;</item>
///   <item>same key while the first is still running: <c>409</c>; same key with a different body: <c>422</c>;</item>
///   <item>store unavailable: <c>503</c>, because exactly-once can no longer be guaranteed.</item>
/// </list>
/// Keys are scoped to the caller (<c>sub</c>), so consumers can never see or block each other's keys. The header is
/// optional: requests without it behave as before.
/// </summary>
internal sealed class IdempotencyFilter(IIdempotencyStore store, ProblemDetailsFactory problemDetailsFactory)
    : IAsyncResourceFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    /// <summary>HttpContext item holding the consumer-scoped key, read by <c>ICurrentActor.RequestKey</c>.</summary>
    public const string RequestKeyItem = "IdempotencyRequestKey";
    public const int MaxKeyLength = 128;

    private static readonly SearchValues<char> AllowedKeyCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.:");

    // Headers that are part of the response a client relies on, replayed with the body.
    private static readonly string[] ReplayedHeaders = ["ETag", "Location"];

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            await next();
            return;
        }

        var key = values.ToString();
        if (key.Length is 0 or > MaxKeyLength || key.AsSpan().ContainsAnyExcept(AllowedKeyCharacters))
        {
            context.Result = Problem(
                http,
                StatusCodes.Status400BadRequest,
                "idempotency_key_invalid",
                $"'{HeaderName}' must be 1-{MaxKeyLength} characters of letters, digits, '-', '_', '.' or ':'.");
            return;
        }

        var scopedKey = $"{http.User.FindFirst("sub")?.Value ?? "anonymous"}:{key}";
        // Also recorded with the change in the database (unique), the backstop if this claim is ever lost.
        http.Items[RequestKeyItem] = scopedKey;
        var fingerprint = await FingerprintAsync(http.Request);
        var claim = await store.TryBeginAsync(scopedKey, fingerprint, http.RequestAborted);

        switch (claim.Status)
        {
            case IdempotencyClaimStatus.Completed:
                context.Result = Replay(http, claim.Response!);
                return;
            case IdempotencyClaimStatus.InProgress:
                http.Response.Headers.RetryAfter = "1";
                context.Result = Problem(
                    http,
                    StatusCodes.Status409Conflict,
                    "idempotency_key_in_progress",
                    "A request with this Idempotency-Key is still being processed. Retry shortly.");
                return;
            case IdempotencyClaimStatus.FingerprintMismatch:
                context.Result = Problem(
                    http,
                    StatusCodes.Status422UnprocessableEntity,
                    "idempotency_key_reused",
                    "This Idempotency-Key was already used for a different request.");
                return;
            case IdempotencyClaimStatus.Unavailable:
                context.Result = Problem(
                    http,
                    StatusCodes.Status503ServiceUnavailable,
                    "idempotency_unavailable",
                    "The request cannot be guaranteed to run only once right now. Retry later with the same key.");
                return;
        }

        await ExecuteAndRememberAsync(http, next, scopedKey, fingerprint);
    }

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

    /// <summary>Method, path, query and body: the same key with a different request is detected.</summary>
    private static async Task<string> FingerprintAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        request.Body.Position = 0;

        var material = $"{request.Method}\n{request.Path}{request.QueryString}\n{body}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private ObjectResult Problem(HttpContext http, int status, string errorCode, string detail)
    {
        var problem = problemDetailsFactory.CreateProblemDetails(
            http,
            status,
            title: ReasonPhrases.GetReasonPhrase(status),
            detail: detail);
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { ApiControllerBase.ProblemJsonContentType },
        };
    }
}
