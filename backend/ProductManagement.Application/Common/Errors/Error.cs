namespace ProductManagement.Application.Common.Errors;

/// <summary>
/// Describes an expected failure. It is returned as a value (see <see cref="Results.Result{TValue}"/>)
/// instead of thrown, so the possible outcomes of an operation show up in its signature.
/// </summary>
public sealed record Error
{
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

    /// <summary>Stable, machine-readable identifier (e.g. <c>product.insufficient_stock</c>). Clients may branch on it.</summary>
    public string Code { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

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

    /// <summary>The <see cref="Code"/> of every validation error.</summary>
    public const string ValidationFailedCode = "validation_failed";

    public static Error Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ValidationFailedCode, "One or more validation errors occurred.", ErrorType.Validation)
        {
            ValidationErrors = errors,
        };

    public static Error Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}
