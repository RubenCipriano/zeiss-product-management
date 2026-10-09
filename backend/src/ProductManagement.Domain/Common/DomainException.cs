namespace ProductManagement.Domain.Common;

/// <summary>
/// Raised when an operation would violate a domain invariant.
/// </summary>
/// <remarks>
/// The application layer validates input before it reaches the domain, so in practice this exception
/// signals a programming error (defence in depth) rather than a user mistake.
/// </remarks>
public sealed class DomainException : Exception
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
