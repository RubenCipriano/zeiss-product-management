using System.Security.Cryptography;
using ProductManagement.Api.Security;

namespace ProductManagement.UnitTests.Api.Security;

public class AuthenticationOptionsValidatorTests
{
    private static readonly string Pem = CreatePem(ECCurve.NamedCurves.nistP256);

    private readonly AuthenticationOptionsValidator _validator = new();

    [Fact]
    public void AValidKeyAndConsumers_Pass()
    {
        var options = new AuthenticationOptions
        {
            SigningKey = Pem,
            Consumers = [new ApiConsumer { ClientId = "clerk", SecretHash = SecretHasher.Hash("secret") }],
        };

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "is missing")]
    [InlineData("not a key", "is not a valid PEM-encoded ECDSA private key")]
    public void AMissingOrUnreadableSigningKey_Fails(string signingKey, string reason)
    {
        var result = _validator.Validate(null, new AuthenticationOptions { SigningKey = signingKey });

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(reason);
    }

    [Fact]
    public void AKeyOnAnotherCurve_Fails()
    {
        var result = _validator.Validate(null, new AuthenticationOptions { SigningKey = CreatePem(ECCurve.NamedCurves.nistP384) });

        result.FailureMessage!.ShouldContain("must be a P-256 key");
    }

    [Fact]
    public void AConsumerWithoutAHashedSecret_Fails()
    {
        var options = new AuthenticationOptions
        {
            SigningKey = Pem,
            Consumers = [new ApiConsumer { ClientId = "clerk", SecretHash = "plain-text-secret" }],
        };

        _validator.Validate(null, options).FailureMessage!.ShouldContain("API consumer 'clerk' needs a client id and a secret hash");
    }

    private static string CreatePem(ECCurve curve)
    {
        using var key = ECDsa.Create(curve);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
