using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Redis;

/// <summary>
/// The exceptions every user of Redis treats as "Redis is unavailable right now" and handles by degrading (fail open,
/// or fail closed for idempotency) instead of failing the request.
/// </summary>
internal static class RedisFailures
{
    /// <remarks>
    /// <see cref="ObjectDisposedException"/>: the connection is already disposed while the host shuts down.
    /// </remarks>
    public static bool IsTransient(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException;
}
