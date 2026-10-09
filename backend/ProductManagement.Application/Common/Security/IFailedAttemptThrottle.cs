namespace ProductManagement.Application.Common.Security;

/// <summary>
/// Counts failed sign-in attempts per key (e.g. client IP) across all API replicas, and blocks a key once it has
/// failed too often within a time window. Protects the token endpoint against secret guessing.
/// </summary>
public interface IFailedAttemptThrottle
{
    /// <summary>How long the key is still blocked, or <c>null</c> when it may try again.</summary>
    Task<TimeSpan?> GetBlockedForAsync(string key, CancellationToken cancellationToken);

    Task RecordFailureAsync(string key, CancellationToken cancellationToken);
}
