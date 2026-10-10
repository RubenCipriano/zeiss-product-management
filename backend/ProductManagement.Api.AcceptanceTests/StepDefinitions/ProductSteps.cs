using System.Globalization;
using System.Net;
using ProductManagement.Api.AcceptanceTests.Support;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Testing;
using Reqnroll;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.AcceptanceTests.StepDefinitions;

[Binding]
public class ProductSteps(ScenarioState state)
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

    [When("I create a product with these details:")]
    public async Task WhenICreateAProductWithTheseDetails(DataTable details)
    {
        // Sent as raw JSON so a field can be missing or out of range, as a careless client would send it.
        var request = details.Rows.ToDictionary(row => row["Field"], row => ParseValue(row["Value"]));
        state.RecordResponse(await state.Api.CreateAsync(request));
    }

    [When("I browse the catalogue")]
    public async Task WhenIBrowseTheCatalogue() => state.RecordResponse(await state.Api.ListAsync());

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

    [When(@"^I rename the product to ""(.*)"" and change its price to (\d+\.\d{2})$")]
    public async Task WhenIRenameTheProductAndChangeItsPrice(string name, decimal price)
    {
        var current = await state.Api.GetProductAsync(state.Product.Id);
        var request = TestProducts.UpdateRequest(current, name: name, price: price);
        state.RecordResponse(await state.Api.UpdateAsync(current.Id, request, ifMatch: $"\"{current.Version}\""));
    }

    [When("I delete the product")]
    public async Task WhenIDeleteTheProduct() => state.RecordResponse(await state.Api.DeleteAsync(state.Product.Id));

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

    [Then(@"^the product is named ""(.*)"" and priced (\d+\.\d{2})$")]
    public async Task ThenTheProductIsNamedAndPriced(string name, decimal price)
    {
        var product = await state.Api.GetProductAsync(state.Product.Id);
        product.Name.ShouldBe(name);
        product.Price.ShouldBe(price);
    }

    [Then("the product is deleted")]
    public void ThenTheProductIsDeleted() => state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.NoContent);

    [Then("the product can no longer be found")]
    public async Task ThenTheProductCanNoLongerBeFound()
    {
        using var response = await state.Api.GetAsync(state.Product.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Then("each problem is named:")]
    public async Task ThenEachProblemIsNamed(DataTable expected)
    {
        var errors = (await ReadProblemAsync(state.LastResponse!)).Errors!;

        errors.Keys.ShouldBe(expected.Rows.Select(row => row["Field"]), ignoreOrder: true);
        foreach (var row in expected.Rows)
        {
            errors[row["Field"]].ShouldBe([row["Problem"]]);
        }
    }

    [Then("the request is rejected because the product was modified in the meantime")]
    public async Task ThenTheRequestIsRejectedBecauseTheProductWasModified()
    {
        state.LastResponse!.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(state.LastResponse)).ErrorCode.ShouldBe("product.version_mismatch");
    }

    /// <summary>A number where the cell holds one (so it is sent as a JSON number), otherwise the text itself.</summary>
    private static object ParseValue(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : value;
}
