namespace ProductManagement.Application.Common.Security;

/// <summary>Who is performing the current operation (the API consumer from the access token), for audit records.</summary>
public interface ICurrentActor
{
    string Name { get; }
}
