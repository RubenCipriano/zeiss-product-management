namespace ProductManagement.Application.Common.Errors;

/// <summary>
/// Categories of expected failures. The API layer maps each one to an HTTP status code.
/// </summary>
public enum ErrorType
{
    /// <summary>The request is malformed or breaks a business rule that can be checked without current state.</summary>
    Validation,

    /// <summary>The requested resource does not exist.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state of the resource (e.g. not enough stock).</summary>
    Conflict,

    /// <summary>A precondition supplied by the client (e.g. <c>If-Match</c>) does not hold.</summary>
    PreconditionFailed,
}
