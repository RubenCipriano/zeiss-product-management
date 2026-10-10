using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Api.Security;

/// <summary>The outcome of a sign-in at the token endpoint.</summary>
public abstract record TokenOutcome
{
    private TokenOutcome()
    {
    }

    public record Issued(IssuedToken Token) : TokenOutcome;

    /// <summary>Unknown client or wrong secret; deliberately not told apart.</summary>
    public record InvalidCredentials : TokenOutcome;

    /// <summary>The caller failed too often recently and may not try again for <paramref name="RetryAfter"/>.</summary>
    public record Blocked(TimeSpan RetryAfter) : TokenOutcome;
}

/// <summary>
/// The client-credentials sign-in: refuses callers that failed too often (cluster-wide, per <c>callerKey</c>), checks
/// the credentials, and counts the failures. The token endpoint only maps the outcome to HTTP.
/// </summary>
public class TokenService(AccessTokenIssuer issuer, IFailedAttemptThrottle failedAttempts)
{
    public async Task<TokenOutcome> IssueAsync(
        string clientId,
        string clientSecret,
        string callerKey,
        CancellationToken cancellationToken)
    {
        if (await failedAttempts.GetBlockedForAsync(callerKey, cancellationToken) is { } blockedFor)
        {
            return new TokenOutcome.Blocked(blockedFor);
        }

        var token = issuer.Authenticate(clientId, clientSecret);
        if (token is null)
        {
            await failedAttempts.RecordFailureAsync(callerKey, cancellationToken);
            return new TokenOutcome.InvalidCredentials();
        }

        return new TokenOutcome.Issued(token);
    }
}
