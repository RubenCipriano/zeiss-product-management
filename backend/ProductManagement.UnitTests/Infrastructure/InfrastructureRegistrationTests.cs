using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Idempotency;
using ProductManagement.Infrastructure.Security;

namespace ProductManagement.UnitTests.Infrastructure;

public class InfrastructureRegistrationTests
{
    private const string Database = "Host=unused;Database=unused";

    [Fact]
    public void WithoutRedis_WhenRedisIsRequired_RefusesToStart()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            Services(Configuration()).AddInfrastructure(Configuration(), requireRedis: true));

        exception.Message.ShouldContain("'Redis' is not configured");
    }

    [Fact]
    public void WithoutRedis_InLocalDevelopment_FallsBackToInMemoryStores()
    {
        var configuration = Configuration();
        using var services = Services(configuration).AddInfrastructure(configuration).BuildServiceProvider();

        services.GetRequiredService<ICatalogCache>().ShouldBeOfType<NullCatalogCache>();
        services.GetRequiredService<IIdempotencyStore>().ShouldBeOfType<InMemoryIdempotencyStore>();
        services.GetRequiredService<IFailedAttemptThrottle>().ShouldBeOfType<InMemoryFailedAttemptThrottle>();
        services.GetRequiredService<IDistributedRateLimiter>().ShouldBeOfType<LocalOnlyRateLimiter>();
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        var configuration = Configuration(("SignInThrottle:MaxFailures", "0"), ("Idempotency:KeyLifetime", "00:00:00"));
        using var services = Services(configuration).AddInfrastructure(configuration).BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<FailedAttemptThrottleOptions>>().Value)
            .Message.ShouldContain("MaxFailures");
        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<IdempotencyOptions>>().Value)
            .Message.ShouldContain("KeyLifetime");
    }

    [Fact]
    public void RedisPassword_SetOnItsOwn_IsAddedToTheConnection()
    {
        var redis = InfrastructureServiceCollectionExtensions.ParseRedisConfiguration("cache.internal:6379", "from-secret");

        redis.Password.ShouldBe("from-secret");
        redis.EndPoints.ShouldHaveSingleItem();
    }

    [Fact]
    public void RedisPassword_SetOnItsOwn_WinsOverThePasswordInTheConnectionString()
    {
        var redis = InfrastructureServiceCollectionExtensions.ParseRedisConfiguration(
            "cache.internal:6379,password=old", "from-secret");

        redis.Password.ShouldBe("from-secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutASeparateRedisPassword_TheConnectionStringIsUsedAsItIs(string? password)
    {
        var redis = InfrastructureServiceCollectionExtensions.ParseRedisConfiguration(
            "cache.internal:6379,password=in-the-string", password);

        redis.Password.ShouldBe("in-the-string");
    }

    // What a host provides: options are bound from the registered configuration.
    private static IServiceCollection Services(IConfiguration configuration) =>
        new ServiceCollection().AddLogging().AddSingleton(configuration);

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(setting => KeyValuePair.Create(setting.Key, (string?)setting.Value))
                    .Append(KeyValuePair.Create("ConnectionStrings:ProductManagement", (string?)Database)))
            .Build();
}
