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
    /// <see cref="OperationCanceledException"/>: StackExchange.Redis takes no cancellation token, so a cancelled
    /// command is one the client library abandoned (seen on a new replica's first request), never the caller giving
    /// up. Unhandled, it became a 500 for a client that was still waiting.
    /// </remarks>
    public static bool IsTransient(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException or OperationCanceledException;
}
