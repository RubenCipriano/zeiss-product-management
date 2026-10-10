namespace ProductManagement.Testing;

/// <summary>
/// The subset of an RFC 9457 problem response the tests assert on.
/// </summary>
public record ApiProblem(
    int Status,
    string? Title,
    string? Detail,
    string? ErrorCode,
    Dictionary<string, string[]>? Errors,
    int? Requested,
    int? Available);
