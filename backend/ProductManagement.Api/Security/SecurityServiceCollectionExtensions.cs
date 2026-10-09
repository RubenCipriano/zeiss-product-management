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
        var signingKey = Validate(settings);

        services.Configure<AuthenticationSettings>(section);
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
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "roles",
                };
            });

        services.AddAuthorization(Policies.Configure);

        return services;
    }

    /// <summary>Fail fast: an API that starts with a weak key or broken consumers is worse than one that does not start.</summary>
    private static byte[] Validate(AuthenticationSettings settings)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(settings.SigningKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"'{AuthenticationSettings.SectionName}:SigningKey' must be Base64.");
        }

        if (key.Length < AuthenticationSettings.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"'{AuthenticationSettings.SectionName}:SigningKey' must be at least {AuthenticationSettings.MinimumSigningKeyBytes} bytes.");
        }

        foreach (var consumer in settings.Consumers)
        {
            if (string.IsNullOrWhiteSpace(consumer.ClientId) || !SecretHasher.IsWellFormed(consumer.SecretHash))
            {
                throw new InvalidOperationException(
                    $"API consumer '{consumer.ClientId}' needs a client id and a secret hash (use the hash-secret command).");
            }
        }

        return key;
    }
}
