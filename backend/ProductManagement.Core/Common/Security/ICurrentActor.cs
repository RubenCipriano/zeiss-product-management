namespace ProductManagement.Core.Common.Security;

/// <summary>
/// Who is making the current request: the client id from the access token. Recorded on every stock ledger entry.
/// </summary>
public interface ICurrentActor
{
    string Name { get; }
}
