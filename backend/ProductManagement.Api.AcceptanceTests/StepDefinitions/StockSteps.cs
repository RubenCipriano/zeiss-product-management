using System.Net;
using ProductManagement.Api.AcceptanceTests.Support;
using Reqnroll;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.AcceptanceTests.StepDefinitions;

[Binding]
public sealed class StockSteps(ScenarioState state)
{
    [When("I remove {int} units from stock")]
    [When("someone else removes {int} unit(s) from stock")]
    public async Task WhenIRemoveUnitsFromStock(int quantity) =>
        state.RecordResponse(await state.Api.DecrementStockAsync(state.Product.Id, quantity));

    [When("I add {int} units to stock")]
    public async Task WhenIAddUnitsToStock(int quantity) =>
        state.RecordResponse(await state.Api.AddToStockAsync(state.Product.Id, quantity));

    [When("{int} customers each order {int} unit(s) at the same time")]
    public async Task WhenCustomersOrderAtTheSameTime(int customers, int quantity)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, customers)
            .Select(_ => state.Api.DecrementStockAsync(state.Product.Id, quantity)));
        state.RecordConcurrentResponses(responses);
    }

    [Then("the request succeeds")]
    public void ThenTheRequestSucceeds() => state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.OK);

    [Then("the request is rejected as invalid")]
    public void ThenTheRequestIsRejectedAsInvalid() =>
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Then("the request is rejected as a conflict with error code {string}")]
    public async Task ThenTheRequestIsRejectedAsAConflict(string errorCode)
    {
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(state.LastResponse)).ErrorCode.ShouldBe(errorCode);
    }

    [Then("{int} orders succeed and {int} are rejected")]
    public void ThenOrdersSucceedAndAreRejected(int succeeded, int rejected)
    {
        state.ConcurrentResponses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(succeeded);
        state.ConcurrentResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(rejected);
    }

    [Then("the product has {int} units in stock")]
    public async Task ThenTheProductHasUnitsInStock(int expected) =>
        (await state.Api.GetProductAsync(state.Product.Id)).StockQuantity.ShouldBe(expected);
}
