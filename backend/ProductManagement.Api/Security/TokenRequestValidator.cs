using FluentValidation;

namespace ProductManagement.Api.Security;

/// <summary>
/// Both credentials must be present. Whether they are right is the token service's job, so that a wrong client id and
/// a wrong secret get the same answer.
/// </summary>
internal class TokenRequestValidator : AbstractValidator<TokenRequest>
{
    public TokenRequestValidator()
    {
        RuleFor(request => request.ClientId)
            .NotEmpty();

        // A secret is opaque: any non-empty value is checked against the stored hash.
        RuleFor(request => request.ClientSecret)
            .Must(secret => !string.IsNullOrEmpty(secret))
            .WithMessage("'Client Secret' must not be empty.");
    }
}
