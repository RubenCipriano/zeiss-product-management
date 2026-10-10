namespace ProductManagement.Core.Common.Requests;

/// <summary>
/// Information about the current HTTP request that Core needs without depending on ASP.NET. Implemented in the API
/// (<c>HttpRequestContext</c>).
/// </summary>
public interface IRequestContext
{
    /// <summary>
    /// The request's <c>Idempotency-Key</c> (an id the client attaches so that the same request sent twice is applied
    /// once), prefixed with the caller's id; <c>null</c> if it sent none. Saved on the stock ledger entry the request
    /// creates, under a unique index, so the database itself refuses to apply the request twice.
    /// </summary>
    string? IdempotencyKey { get; }
}
