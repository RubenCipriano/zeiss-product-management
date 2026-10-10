using ProductManagement.Core.Common.Errors;

namespace ProductManagement.Core.Common.Requests;

/// <summary>
/// Failures about the request itself rather than the product it targets. Codes are part of the public API contract.
/// </summary>
public static class RequestErrors
{
    /// <summary>Also returned by the API's idempotency filter, while its store still remembers the key.</summary>
    public const string IdempotencyKeyReusedCode = "idempotency_key_reused";

    /// <summary>
    /// The request's idempotency key is recorded on a change that is not this request's: the key was reused for a
    /// different operation after the idempotency store forgot it.
    /// </summary>
    public static Error IdempotencyKeyReused() =>
        Error.Unprocessable(IdempotencyKeyReusedCode, "This Idempotency-Key was already used for a different request.");
}
