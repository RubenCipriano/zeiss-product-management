namespace ProductManagement.Api.RateLimiting;

public static class RateLimitPolicies
{
    /// <summary>Stricter limit for state-changing endpoints, on top of the global per-caller limit.</summary>
    public const string Writes = "writes";

    /// <summary>Token endpoint: few attempts per client IP, to make guessing secrets impractical.</summary>
    public const string Authentication = "authentication";
}
