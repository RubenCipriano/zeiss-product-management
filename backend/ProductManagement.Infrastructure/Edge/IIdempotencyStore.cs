namespace ProductManagement.Infrastructure.Edge;

/// <summary>A response kept so a retried request can be answered without being executed again.</summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, string Body, IReadOnlyDictionary<string, string> Headers);

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

public sealed record IdempotencyClaim(IdempotencyClaimStatus Status, StoredResponse? Response = null);

/// <summary>
/// Shared record of idempotency keys, used by every API replica, so a request retried against a different replica is
/// still recognised.
/// </summary>
/// <remarks>
/// An HTTP-edge port: the API's idempotency filter is its only user and what it stores is an HTTP response, so it
/// lives next to its implementations rather than in the application layer: no use case needs it.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Atomically claims <paramref name="key"/> for a request with the given fingerprint.</summary>
    Task<IdempotencyClaim> TryBeginAsync(string key, string fingerprint, CancellationToken cancellationToken);

    Task CompleteAsync(string key, string fingerprint, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Releases the key after a failure, so the client can retry.</summary>
    Task AbandonAsync(string key, CancellationToken cancellationToken);
}
