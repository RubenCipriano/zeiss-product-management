using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProductManagement.Api.Idempotency;
using ProductManagement.Core.Common.Requests;
using ProductManagement.Core.Common.Security;

namespace ProductManagement.Api.Security;

/// <summary>
/// Registers sign-in and access control at startup (<c>AddApiSecurity</c> in <c>Program.cs</c>): token issuing, how
/// incoming bearer tokens are verified (authentication), and which roles may call what (authorization,
/// <see cref="Policies"/>).
/// </summary>
internal static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        services.AddOptions<AuthenticationOptions>()
            .BindConfiguration(AuthenticationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthenticationOptions>, AuthenticationOptionsValidator>();

        services.AddSingleton(provider =>
            TokenSigningKey.FromPem(provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.SigningKey));
        services.AddSingleton<AccessTokenIssuer>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<IValidator<TokenRequest>, TokenRequestValidator>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
        services.AddScoped<IRequestContext, HttpRequestContext>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthenticationOptions>, TokenSigningKey>((options, authentication, signingKey) =>
            {
                // Keep claim names as issued (sub, roles) instead of the legacy WS-Federation URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authentication.Value.Issuer,
                    ValidateAudience = true,
                    ValidAudience = authentication.Value.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey.VerificationKey,
                    // ES256 only: a token claiming another algorithm (HS256 "signed" with the public key, none, RS256)
                    // is rejected before any signature check (algorithm confusion).
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = TokenClaims.Subject,
                    RoleClaimType = TokenClaims.Roles,
                };
            });

        services.AddAuthorization(Policies.Configure);

        return services;
    }
}
