namespace ProductManagement.Api.RateLimiting;

/// <summary>
/// Token-bucket limits per caller (the signed-in user, or the client IP for anonymous requests).
/// </summary>
internal sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    /// <summary>Sustained requests per second per caller, for all endpoints.</summary>
    public int RequestsPerSecond { get; init; } = 50;

    /// <summary>Short bursts allowed above the sustained rate (bucket size).</summary>
    public int RequestBurst { get; init; } = 100;

    /// <summary>Sustained writes per second per caller (create, update, delete, stock changes).</summary>
    public int WritesPerSecond { get; init; } = 10;

    public int WriteBurst { get; init; } = 20;
}
