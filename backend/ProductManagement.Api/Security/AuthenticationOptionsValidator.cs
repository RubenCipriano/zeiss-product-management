using Microsoft.Extensions.Options;

namespace ProductManagement.Api.Security;

/// <summary>
/// Fail fast: an API that starts with a missing or bad signing key, or with broken consumers, is worse than one that
/// does not start.
/// </summary>
internal class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        var failures = new List<string>();
        try
        {
            using var signingKey = TokenSigningKey.FromPem(options.SigningKey);
        }
        catch (InvalidOperationException exception)
        {
            failures.Add(exception.Message);
        }

        failures.AddRange(options.Consumers
            .Where(consumer =>
                string.IsNullOrWhiteSpace(consumer.ClientId) || !SecretHasher.IsWellFormed(consumer.SecretHash))
            .Select(consumer =>
                $"API consumer '{consumer.ClientId}' needs a client id and a secret hash " +
                "(use the hash-secret command)."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
