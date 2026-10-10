using ProductManagement.Core.Products.Contracts;
using ProductManagement.Testing;

namespace ProductManagement.Api.AcceptanceTests.Support;

/// <summary>
/// Per-scenario state shared between step classes (created and disposed by Reqnroll's context injection).
/// </summary>
public class ScenarioState : IDisposable
{
    public ProductApiClient Api { get; } = TestRunHooks.Environment.CreateClient();

    public ProductResponse? CurrentProduct { get; set; }

    /// <summary>The product as it was when "I" opened it for editing.</summary>
    public ProductResponse? ProductBeingEdited { get; set; }

    public HttpResponseMessage? LastResponse { get; private set; }

    public IReadOnlyList<HttpResponseMessage> ConcurrentResponses { get; private set; } = [];

    public ProductResponse Product =>
        CurrentProduct ?? throw new InvalidOperationException("No product has been set up in this scenario.");

    public void RecordResponse(HttpResponseMessage response)
    {
        LastResponse?.Dispose();
        LastResponse = response;
    }

    public void RecordConcurrentResponses(IReadOnlyList<HttpResponseMessage> responses) => ConcurrentResponses = responses;

    public void Dispose()
    {
        LastResponse?.Dispose();
        foreach (var response in ConcurrentResponses)
        {
            response.Dispose();
        }

        Api.Dispose();
    }
}
