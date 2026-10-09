using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;
using ProductManagement.Api.Security;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Api.UnitTests.Security;

public sealed class TokenServiceTests : IDisposable
{
    private const string CallerKey = "ip:10.0.0.7";

    // PBKDF2 is deliberately slow: hash once for the class.
    private static readonly string ClerkSecretHash = SecretHasher.Hash("clerk-secret");

    private readonly IFailedAttemptThrottle _failedAttempts = Substitute.For<IFailedAttemptThrottle>();
    private readonly TokenSigningKey _signingKey;
    private readonly TokenService _service;

    public TokenServiceTests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _signingKey = TokenSigningKey.FromPem(key.ExportPkcs8PrivateKeyPem());
        var options = new AuthenticationOptions
        {
            Consumers = [new ApiConsumer { ClientId = "clerk", SecretHash = ClerkSecretHash, Roles = [Roles.StockClerk] }],
        };
        var issuer = new AccessTokenIssuer(
            Options.Create(options),
            _signingKey,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)));
        _service = new TokenService(issuer, _failedAttempts);
    }

    public void Dispose() => _signingKey.Dispose();

    [Fact]
    public async Task ValidCredentials_GetATokenForThatConsumerAndItsRoles()
    {
        var outcome = await _service.IssueAsync("clerk", "clerk-secret", CallerKey, TestContext.Current.CancellationToken);

        var issued = outcome.ShouldBeOfType<TokenOutcome.Issued>();
        issued.Token.ExpiresInSeconds.ShouldBe(3600);
        var token = new JsonWebToken(issued.Token.AccessToken);
        token.GetClaim(TokenClaims.Subject).Value.ShouldBe("clerk");
        token.GetClaim(TokenClaims.Roles).Value.ShouldBe(Roles.StockClerk);
        token.Alg.ShouldBe("ES256");
        await _failedAttempts.DidNotReceiveWithAnyArgs().RecordFailureAsync(default!, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("clerk", "wrong-secret")]
    [InlineData("unknown", "clerk-secret")]
    public async Task InvalidCredentials_AreRefusedAlikeAndCountedAgainstTheCaller(string clientId, string secret)
    {
        var outcome = await _service.IssueAsync(clientId, secret, CallerKey, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<TokenOutcome.InvalidCredentials>();
        await _failedAttempts.Received(1).RecordFailureAsync(CallerKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABlockedCaller_IsRefusedWithoutCheckingTheCredentials()
    {
        _failedAttempts.GetBlockedForAsync(CallerKey, Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(42));

        var outcome = await _service.IssueAsync("clerk", "clerk-secret", CallerKey, TestContext.Current.CancellationToken);

        outcome.ShouldBe(new TokenOutcome.Blocked(TimeSpan.FromSeconds(42)));
        await _failedAttempts.DidNotReceiveWithAnyArgs().RecordFailureAsync(default!, Arg.Any<CancellationToken>());
    }
}
