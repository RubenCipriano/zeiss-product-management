using System.Collections.Concurrent;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Makes simultaneous cache misses for the same key share one database query (per replica). Without it, when a
/// popular entry expires, every request in flight runs the same expensive query at once (a "cache stampede";
/// measured in docs/adr/0008-redis-cache.md).
/// </summary>
internal class SingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="load"/>, or waits for the run already in progress for <paramref name="key"/>. If that run
    /// fails (for example because its request was cancelled), a waiting caller loads for itself.
    /// </summary>
    public async Task<T?> RunAsync<T>(string key, Func<Task<T?>> load)
        where T : class
    {
        var mine = new Lazy<Task<object?>>(async () => await load());
        var current = _inFlight.GetOrAdd(key, mine);
        var isLeader = ReferenceEquals(current, mine);
        try
        {
            return (T?)await current.Value;
        }
        catch (Exception) when (!isLeader)
        {
            return await load();
        }
        finally
        {
            if (isLeader)
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, mine));
            }
        }
    }
}
