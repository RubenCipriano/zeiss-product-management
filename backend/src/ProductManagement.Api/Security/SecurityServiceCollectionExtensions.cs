using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace ProductManagement.Api.Security;

internal static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(AuthenticationSettings.SectionName).Get<AuthenticationSettings>()
            ?? new AuthenticationSettings();

        // Fail fast: an API that silently starts without token validation is worse than one that does not start.
        if (string.IsNullOrWhiteSpace(settings.Authority))
        {
            throw new InvalidOperationException(
                $"'{AuthenticationSettings.SectionName}:Authority' is not configured.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.Authority;
                if (!string.IsNullOrWhiteSpace(settings.MetadataAddress))
                {
                    options.MetadataAddress = settings.MetadataAddress;
                }

                options.Audience = settings.Audience;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;

                // Keep the claim names exactly as issued (sub, roles, preferred_username) instead of the legacy
                // WS-Federation URIs, so policies and logs use the same names as the token.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            });

        services.AddAuthorization(Policies.Configure);

        return services;
    }
}
