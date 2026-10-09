using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ProductManagement.Application.Common.Security;

namespace ProductManagement.Api.Security;

internal static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthenticationSettings.SectionName);
        var settings = section.Get<AuthenticationSettings>() ?? new AuthenticationSettings();
        ValidateConsumers(settings);
        var signingKey = TokenSigningKey.FromPem(settings.SigningKey);

        services.Configure<AuthenticationSettings>(section);
        services.AddSingleton(signingKey);
        services.AddSingleton<AccessTokenIssuer>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpContextCurrentActor>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep claim names as issued (sub, roles) instead of the legacy WS-Federation URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey.VerificationKey,
                    // ES256 only: a token claiming another algorithm (HS256 "signed" with the public key, none, RS256)
                    // is rejected before any signature check (algorithm confusion).
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "roles",
                };
            });

        services.AddAuthorization(Policies.Configure);

        return services;
    }

    /// <summary>Fail fast: an API that starts with a bad key or broken consumers is worse than one that does not start.</summary>
    private static void ValidateConsumers(AuthenticationSettings settings)
    {
        foreach (var consumer in settings.Consumers)
        {
            if (string.IsNullOrWhiteSpace(consumer.ClientId) || !SecretHasher.IsWellFormed(consumer.SecretHash))
            {
                throw new InvalidOperationException(
                    $"API consumer '{consumer.ClientId}' needs a client id and a secret hash (use the hash-secret command).");
            }
        }
    }
}
