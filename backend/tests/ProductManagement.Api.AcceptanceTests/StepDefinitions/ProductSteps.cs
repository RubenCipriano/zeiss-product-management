using System.Globalization;
using System.Net;
using ProductManagement.Api.AcceptanceTests.Support;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Testing;
using Reqnroll;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.AcceptanceTests.StepDefinitions;

[Binding]
public sealed class ProductSteps(ScenarioState state)
{
    [Given("a product {string} with {int} units in stock")]
    public async Task GivenAProductWithStock(string name, int stock) =>
        state.CurrentProduct = await state.Api.CreateProductAsync(TestProducts.CreateRequest(name: name, stockQuantity: stock));

    [Given("the following products exist:")]
    public async Task GivenTheFollowingProductsExist(DataTable table)
    {
        foreach (var row in table.Rows)
        {
            await state.Api.CreateProductAsync(TestProducts.CreateRequest(
                name: row["Name"],
                stockQuantity: int.Parse(row["Stock"], CultureInfo.InvariantCulture)));
        }
    }

    [Given("I have opened the product for editing")]
    public async Task GivenIHaveOpenedTheProductForEditing() =>
        state.ProductBeingEdited = await state.Api.GetProductAsync(state.Product.Id);

    [When(@"^I create a product named ""(.*)"" priced (\d+\.\d{2}) with (\d+) units in stock$")]
    public async Task WhenICreateAProduct(string name, decimal price, int stock)
    {
        var response = await state.Api.CreateAsync(TestProducts.CreateRequest(name: name, price: price, stockQuantity: stock));
        state.RecordResponse(response);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            state.CurrentProduct = await ReadAsync<ProductResponse>(response);
        }
    }

    [When("I search for {string}")]
    public async Task WhenISearchFor(string term) => state.RecordResponse(await state.Api.SearchAsync(term));

    [When("I look for products with between {int} and {int} units in stock")]
    public async Task WhenILookForProductsWithStockBetween(int min, int max) =>
        state.RecordResponse(await state.Api.ByStockLevelAsync(min, max));

    [When("I save my changes to the product")]
    public async Task WhenISaveMyChanges()
    {
        var edited = state.ProductBeingEdited
            ?? throw new InvalidOperationException("The product was not opened for editing.");

        var request = TestProducts.UpdateRequest(edited, name: edited.Name + " (edited)");
        state.RecordResponse(await state.Api.UpdateAsync(edited.Id, request, ifMatch: $"\"{edited.Version}\""));
    }

    [Then("the product is created with a six-digit identifier")]
    public void ThenTheProductIsCreatedWithASixDigitIdentifier()
    {
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.Created);
        state.Product.Id.ToString(CultureInfo.InvariantCulture).Length.ShouldBe(6);
    }

    [Then("I see these products:")]
    public async Task ThenISeeTheseProducts(DataTable expected)
    {
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await ReadPageAsync(state.LastResponse);

        var actual = page.Items.Select(product => (product.Name, product.StockQuantity)).ToList();
        var wanted = expected.Rows
            .Select(row => (row["Name"], int.Parse(row["Stock"], CultureInfo.InvariantCulture)))
            .ToList();
        actual.ShouldBe(wanted);
    }

    [Then("the request is rejected because the product was modified in the meantime")]
    public async Task ThenTheRequestIsRejectedBecauseTheProductWasModified()
    {
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(state.LastResponse)).ErrorCode.ShouldBe("product.version_mismatch");
    }
}
