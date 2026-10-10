using System.Globalization;
using ProductManagement.Core.Common.Errors;
using ProductManagement.Core.Common.Requests;

namespace ProductManagement.Api.Http;

/// <summary>
/// The <c>errorCode</c>s produced at the HTTP edge (filters, middleware, the token endpoint), as opposed to the ones
/// use cases return (<see cref="Error.Code"/>). Part of the public contract: clients branch on them.
/// </summary>
internal static class ApiErrorCodes
{
    public const string ValidationFailed = Error.ValidationFailedCode;

    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";

    public const string IdempotencyKeyInProgress = "idempotency_key_in_progress";

    public const string IdempotencyKeyReused = RequestErrors.IdempotencyKeyReusedCode;

    public const string IdempotencyUnavailable = "idempotency_unavailable";

    public const string RateLimited = "rate_limited";

    public const string InvalidClient = "invalid_client";

    public const string TooManyFailedAttempts = "too_many_failed_attempts";

    public const string InternalError = "internal_error";

    /// <summary>
    /// The generic code of a framework-generated problem (unknown route, 405, 415, ...): <c>http_&lt;status&gt;</c>.
    /// </summary>
    public static string ForStatus(int status) => string.Create(CultureInfo.InvariantCulture, $"http_{status}");
}
