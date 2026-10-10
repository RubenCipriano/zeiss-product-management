namespace ProductManagement.Core.Common.Persistence;

/// <summary>
/// Thrown when a save finds that the row was changed or deleted by someone else after we read it (an "optimistic
/// concurrency" conflict: nothing is locked while editing; the conflict is detected when saving).
/// </summary>
/// <remarks>
/// Infrastructure converts EF Core's own exception into this one, so Core does not depend on EF Core.
/// </remarks>
public class ConcurrencyConflictException : Exception
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
