namespace ProductManagement.Application.Common.Persistence;

/// <summary>
/// The request (identified by its idempotency key) was already applied: the database refused to record it twice.
/// </summary>
public sealed class DuplicateRequestException : Exception
{
    public DuplicateRequestException()
    {
    }

    public DuplicateRequestException(string message)
        : base(message)
    {
    }

    public DuplicateRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
