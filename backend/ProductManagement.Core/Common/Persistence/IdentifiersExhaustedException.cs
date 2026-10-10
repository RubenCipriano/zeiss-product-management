namespace ProductManagement.Core.Common.Persistence;

/// <summary>
/// Thrown when every six-digit product id has been handed out (ids are never reused).
/// </summary>
/// <remarks>
/// Infrastructure converts the database error into this one; the create use case turns it into an expected failure
/// (503).
/// </remarks>
public class IdentifiersExhaustedException : Exception
{
    public IdentifiersExhaustedException()
    {
    }

    public IdentifiersExhaustedException(string message)
        : base(message)
    {
    }

    public IdentifiersExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
