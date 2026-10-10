namespace ProductManagement.Infrastructure.Edge;

/// <summary>A response kept so a retried request can be answered without being executed again.</summary>
public record StoredResponse(
    int StatusCode,
    string? ContentType,
    string Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>What the store knows about an idempotency key when a request tries to claim it.</summary>
public enum IdempotencyClaimStatus
{
    /// <summary>First time this key is seen: the caller must process the request, then complete or abandon.</summary>
    Started,

    /// <summary>Already processed: replay <see cref="IdempotencyClaim.Response"/>.</summary>
    Completed,

    /// <summary>Another request with this key is being processed right now (on any replica).</summary>
    InProgress,

    /// <summary>The key was used before for a different request.</summary>
    FingerprintMismatch,

    /// <summary>The store cannot be reached, so exactly-once cannot be guaranteed.</summary>
    Unavailable,
}

/// <summary>The answer to a claim: the key's status, and the stored response when it was already processed.</summary>
public record IdempotencyClaim(IdempotencyClaimStatus Status, StoredResponse? Response = null);

/// <summary>
/// Remembers which idempotency keys were used and the response each one got, so a retried request (same key) is
/// answered from memory instead of running twice. Shared by every API replica, so a retry that reaches a different
/// replica is still recognised.
/// </summary>
/// <remarks>
/// It lives here rather than in Core because no use case needs it: its only user is the API's idempotency filter, and
/// what it stores is an HTTP response. See ADR 0017 (idempotency keys).
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Atomically claims <paramref name="key"/> for a request with the given fingerprint.</summary>
    Task<IdempotencyClaim> TryBeginAsync(string key, string fingerprint, CancellationToken cancellationToken);

    Task CompleteAsync(string key, string fingerprint, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Releases the key after a failure, so the client can retry.</summary>
    Task AbandonAsync(string key, CancellationToken cancellationToken);
}
