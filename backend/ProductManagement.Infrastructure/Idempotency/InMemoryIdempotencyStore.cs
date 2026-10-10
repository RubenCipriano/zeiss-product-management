using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Infrastructure.Idempotency;

/// <summary>
/// Single-process fallback when no Redis is configured (local development only). Keys expire like in Redis: a claim
/// after <see cref="IdempotencyOptions.InProgressTimeout"/>, a stored response after
/// <see cref="IdempotencyOptions.KeyLifetime"/>. Expired keys are purged on access.
/// </summary>
internal class InMemoryIdempotencyStore(IOptions<IdempotencyOptions> options, TimeProvider timeProvider)
    : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IdempotencyOptions _options = options.Value;

    public Task<IdempotencyClaim> TryBeginAsync(string key, string fingerprint, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpired(now);

        if (_entries.TryAdd(key, new Entry(fingerprint, null, now + _options.InProgressTimeout)))
        {
            return Task.FromResult(new IdempotencyClaim(IdempotencyClaimStatus.Started));
        }

        // Abandoned in between: treat as in progress, like the Redis store; the client's retry will claim it.
        if (!_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult(new IdempotencyClaim(IdempotencyClaimStatus.InProgress));
        }

        return Task.FromResult(
            entry.Fingerprint != fingerprint ? new IdempotencyClaim(IdempotencyClaimStatus.FingerprintMismatch)
            : entry.Response is null ? new IdempotencyClaim(IdempotencyClaimStatus.InProgress)
            : new IdempotencyClaim(IdempotencyClaimStatus.Completed, entry.Response));
    }

    public Task CompleteAsync(
        string key,
        string fingerprint,
        StoredResponse response,
        CancellationToken cancellationToken)
    {
        _entries[key] = new Entry(fingerprint, response, timeProvider.GetUtcNow() + _options.KeyLifetime);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(string key, CancellationToken cancellationToken)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var entry in _entries)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                // Removes only this exact entry, never one that replaced it in the meantime.
                _entries.TryRemove(entry);
            }
        }
    }

    private record Entry(string Fingerprint, StoredResponse? Response, DateTimeOffset ExpiresAt);
}
