namespace ProductManagement.Application.Common.Persistence;

/// <summary>
/// Persistence-agnostic signal that an optimistic concurrency check failed: the row changed or was deleted after
/// it was read. Infrastructure translates provider-specific exceptions into this one, so the application layer
/// does not depend on EF Core.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
