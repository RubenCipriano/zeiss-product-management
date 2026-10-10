namespace ProductManagement.Core.Common.Persistence;

/// <summary>
/// Thrown when a save finds that this request was already applied: its idempotency key (the id a client attaches so
/// that the same request sent twice is applied once) is already in the stock ledger, and the database refused to
/// record it twice.
/// </summary>
public class DuplicateRequestException : Exception
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
