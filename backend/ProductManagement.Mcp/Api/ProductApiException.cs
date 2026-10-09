namespace ProductManagement.Mcp.Api;

/// <summary>The API rejected a request (validation, not found, conflict, forbidden, rate limited, ...).</summary>
internal sealed class ProductApiException : Exception
{
    public ProductApiException(int status, string? errorCode, string message)
        : base(message)
    {
        Status = status;
        ErrorCode = errorCode;
    }

    public ProductApiException()
    {
    }

    public ProductApiException(string message)
        : base(message)
    {
    }

    public ProductApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Status { get; }

    public string? ErrorCode { get; }
}
