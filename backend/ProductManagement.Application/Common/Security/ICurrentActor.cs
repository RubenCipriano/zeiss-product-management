namespace ProductManagement.Application.Common.Security;

/// <summary>Who is performing the current operation (the API consumer from the access token), for audit records.</summary>
public interface ICurrentActor
{
    string Name { get; }

    /// <summary>
    /// The current request's <c>Idempotency-Key</c>, scoped to this actor, or <c>null</c> if it sent none. Recorded on
    /// the ledger entry the request creates, under a unique index.
    /// </summary>
    string? RequestKey { get; }
}
