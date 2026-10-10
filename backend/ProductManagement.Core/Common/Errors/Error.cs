namespace ProductManagement.Core.Common.Errors;

/// <summary>
/// An expected failure (not found, invalid input, conflict, ...): a stable code, a message and its kind.
/// </summary>
/// <remarks>
/// Use cases <em>return</em> it inside a <see cref="Results.Result{TValue}"/> instead of throwing it, so the ways an
/// operation can fail are visible in its signature. The API turns it into an HTTP error response
/// (<c>ApiControllerBase.Problem</c>).
/// </remarks>
public record Error
{
    /// <summary>The <see cref="Code"/> of every validation error.</summary>
    public const string ValidationFailedCode = "validation_failed";

    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private static readonly IReadOnlyDictionary<string, object?> NoMetadata =
        new Dictionary<string, object?>();

    private Error(string code, string message, ErrorType type)
    {
        Code = code;
        Message = message;
        Type = type;
    }

    /// <summary>
    /// Stable, machine-readable identifier (e.g. <c>product.insufficient_stock</c>). Clients may branch on it.
    /// </summary>
    public string Code { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>The kind of failure; decides the HTTP status code.</summary>
    public ErrorType Type { get; }

    /// <summary>Field-level validation messages keyed by the (camelCase) request property name.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; private init; } = NoValidationErrors;

    /// <summary>Additional structured data that helps clients react (e.g. the currently available stock).</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; private init; } = NoMetadata;

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(code, message, ErrorType.Conflict) { Metadata = metadata ?? NoMetadata };

    public static Error PreconditionFailed(string code, string message) =>
        new(code, message, ErrorType.PreconditionFailed);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public static Error Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ValidationFailedCode, "One or more validation errors occurred.", ErrorType.Validation)
        {
            ValidationErrors = errors,
        };

    public static Error Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}
