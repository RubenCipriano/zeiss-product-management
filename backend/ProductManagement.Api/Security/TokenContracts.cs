namespace ProductManagement.Api.Security;

/// <summary>Client credentials of a registered API consumer.</summary>
public record TokenRequest
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }
}

/// <summary>A bearer token to send as <c>Authorization: Bearer &lt;accessToken&gt;</c>.</summary>
public record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
