using ProductManagement.Api.Security;

namespace ProductManagement.UnitTests.Api.Security;

public class TokenRequestValidatorTests
{
    private readonly TokenRequestValidator _validator = new();

    [Fact]
    public void BothCredentials_Pass()
    {
        _validator.Validate(new TokenRequest { ClientId = "clerk", ClientSecret = "secret" }).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, null, new[] { "ClientId", "ClientSecret" })]
    [InlineData("  ", "secret", new[] { "ClientId" })]
    [InlineData("clerk", "", new[] { "ClientSecret" })]
    public void MissingCredentials_AreNamed(string? clientId, string? clientSecret, string[] invalid)
    {
        var result = _validator.Validate(new TokenRequest { ClientId = clientId, ClientSecret = clientSecret });

        result.Errors.Select(error => error.PropertyName).ShouldBe(invalid);
    }

    [Fact]
    public void ASecretOfOnlySpaces_IsLeftToTheCredentialCheck()
    {
        _validator.Validate(new TokenRequest { ClientId = "clerk", ClientSecret = "   " }).IsValid.ShouldBeTrue();
    }
}
