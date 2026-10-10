namespace ProductManagement.Core.Common;

/// <summary>
/// Thrown when code tries to put a product, category or stock entry into an invalid state (e.g. a negative price).
/// </summary>
/// <remarks>
/// Requests are validated before they reach these classes, so in practice this exception means a bug (a second safety
/// net), not a user mistake; if it ever escapes, the client gets a 500.
/// </remarks>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DomainException()
    {
    }
}
