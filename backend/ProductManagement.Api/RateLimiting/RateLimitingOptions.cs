using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Api.RateLimiting;

/// <summary>
/// Token-bucket limits per caller (the signed-in user, or the client IP for anonymous requests).
/// </summary>
internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    /// <summary>Sustained requests per second per caller, for all endpoints.</summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerSecond { get; init; } = 50;

    /// <summary>Short bursts allowed above the sustained rate (bucket size).</summary>
    [Range(1, int.MaxValue)]
    public int RequestBurst { get; init; } = 100;

    /// <summary>Sustained writes per second per caller (create, update, delete, stock changes).</summary>
    [Range(1, int.MaxValue)]
    public int WritesPerSecond { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int WriteBurst { get; init; } = 20;

    /// <summary>Token requests per minute per client IP (brute-force protection).</summary>
    [Range(1, int.MaxValue)]
    public int TokenRequestsPerMinute { get; init; } = 20;
}
