using ProductManagement.Testing;
using Reqnroll;

namespace ProductManagement.Api.AcceptanceTests.Support;

/// <summary>
/// Starts PostgreSQL and the API once for the whole run, and gives every scenario an empty product table.
/// </summary>
[Binding]
public class TestRunHooks
{
    private static ApiTestEnvironment? _environment;

    public static ApiTestEnvironment Environment =>
        _environment ?? throw new InvalidOperationException("The test environment has not been started.");

    [BeforeTestRun]
    public static async Task StartEnvironmentAsync() => _environment = await ApiTestEnvironment.StartAsync();

    [AfterTestRun]
    public static async Task StopEnvironmentAsync()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
    }

    [BeforeScenario]
    public static Task ResetAsync() => Environment.ResetAsync();
}
