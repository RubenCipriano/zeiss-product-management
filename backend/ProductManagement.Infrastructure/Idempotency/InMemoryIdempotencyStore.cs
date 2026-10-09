using System.Collections.Concurrent;
using ProductManagement.Application.Common.Idempotency;

namespace ProductManagement.Infrastructure.Idempotency;

/// <summary>Single-process fallback when no Redis is configured (local runs without the cluster).</summary>
internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (string Fingerprint, StoredResponse? Response)> _entries = new();

    public Task<IdempotencyClaim> TryBeginAsync(string key, string fingerprint, CancellationToken cancellationToken)
    {
        if (_entries.TryAdd(key, (fingerprint, null)))
        {
            return Task.FromResult(new IdempotencyClaim(IdempotencyClaimStatus.Started));
        }

        var entry = _entries[key];
        return Task.FromResult(
            entry.Fingerprint != fingerprint ? new IdempotencyClaim(IdempotencyClaimStatus.FingerprintMismatch)
            : entry.Response is null ? new IdempotencyClaim(IdempotencyClaimStatus.InProgress)
            : new IdempotencyClaim(IdempotencyClaimStatus.Completed, entry.Response));
    }

    public Task CompleteAsync(string key, string fingerprint, StoredResponse response, CancellationToken cancellationToken)
    {
        _entries[key] = (fingerprint, response);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(string key, CancellationToken cancellationToken)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
