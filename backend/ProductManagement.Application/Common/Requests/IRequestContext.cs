namespace ProductManagement.Application.Common.Requests;

/// <summary>Facts about the current request that are not about who sent it.</summary>
public interface IRequestContext
{
    /// <summary>
    /// The request's <c>Idempotency-Key</c>, scoped to the caller, or <c>null</c> if it sent none. Recorded on the ledger
    /// entry the request creates, under a unique index, so the request is never applied twice.
    /// </summary>
    string? IdempotencyKey { get; }
}
