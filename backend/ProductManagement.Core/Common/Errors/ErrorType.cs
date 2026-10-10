namespace ProductManagement.Core.Common.Errors;

/// <summary>
/// The kinds of expected failure. Each one becomes an HTTP status code (shown in brackets; the mapping is in
/// <c>ApiControllerBase.Problem</c>).
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// (400) The request is malformed or breaks a business rule that can be checked without current state.
    /// </summary>
    Validation,

    /// <summary>(404) The requested resource does not exist.</summary>
    NotFound,

    /// <summary>(409) The request conflicts with the current state of the resource (e.g. not enough stock).</summary>
    Conflict,

    /// <summary>(412) A precondition supplied by the client (e.g. <c>If-Match</c>) does not hold.</summary>
    PreconditionFailed,

    /// <summary>
    /// (422) The request is well-formed but cannot be processed as sent (e.g. an idempotency key reused for another
    /// request).
    /// </summary>
    Unprocessable,

    /// <summary>
    /// (503) The service cannot fulfil the request at the moment; not the client's fault (e.g. ids exhausted).
    /// </summary>
    Unavailable,
}
