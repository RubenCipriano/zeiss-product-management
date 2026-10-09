using ProductManagement.Testing;

namespace ProductManagement.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Gives every test an empty product table and an empty cache (categories stay, they are reference data) and an API client.
/// Tests within this assembly run sequentially (xunit.runner.json) because they share one database and cache.
/// </summary>
public abstract class ApiTestBase(ApiFixture fixture) : IAsyncLifetime
{
    protected ApiTestEnvironment Environment => fixture.Environment;

    protected ProductApiClient Api { get; } = fixture.Environment.CreateClient();

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.Environment.ResetAsync();

    public ValueTask DisposeAsync()
    {
        Api.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
