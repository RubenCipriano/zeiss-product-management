using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;

[assembly: AssemblyFixture(typeof(ApiFixture))]

namespace ProductManagement.Api.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container and one in-memory API host for the whole test assembly. Starting Docker per test
/// would be slow; isolation comes from resetting data before each test instead (see <see cref="ApiTestBase"/>).
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    private ApiTestEnvironment? _environment;

    public ApiTestEnvironment Environment =>
        _environment ?? throw new InvalidOperationException("The API test environment has not been started.");

    public async ValueTask InitializeAsync() => _environment = await ApiTestEnvironment.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
    }
}
