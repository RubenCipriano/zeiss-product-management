namespace ProductManagement.Api.RateLimiting;

/// <summary>
/// Names of the per-endpoint rate limits, applied with <c>[EnableRateLimiting(...)]</c> on controller actions. Every
/// request is also subject to the global per-caller limit (see <see cref="RateLimitingOptions"/>).
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Stricter limit for state-changing endpoints, on top of the global per-caller limit.</summary>
    public const string Writes = "writes";

    /// <summary>Token endpoint: few attempts per client IP, to make guessing secrets impractical.</summary>
    public const string Authentication = "authentication";
}
