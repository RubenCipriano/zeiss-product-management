using FluentValidation;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products.Validation;

/// <summary>
/// Input rules shared by create and update, written with FluentValidation: each <c>RuleFor</c> states the rules for
/// one field, and every broken rule is reported back (400). Limits come from <see cref="ProductRules"/>, the same
/// source the <see cref="Product"/> class and the database constraints use.
/// </summary>
internal abstract class ProductWriteRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IProductWriteRequest
{
    protected ProductWriteRequestValidator()
    {
        RuleFor(request => request.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("'Name' must not be empty.")
            .MaximumLength(ProductRules.NameMaxLength);

        RuleFor(request => request.Description)
            .MaximumLength(ProductRules.DescriptionMaxLength);

        RuleFor(request => request.Price)
            .NotNull()
            .InclusiveBetween(ProductRules.MinPrice, ProductRules.MaxPrice)
            .Must(price => price is null || ProductRules.HasValidPricePrecision(price.Value))
            .WithMessage($"'Price' must not have more than {ProductRules.PriceDecimalPlaces} decimal places.");

        RuleFor(request => request.StockQuantity)
            .NotNull()
            .InclusiveBetween(0, ProductRules.MaxStockQuantity);

        RuleFor(request => request.CategoryId)
            .NotNull()
            .GreaterThan(0);
    }
}

internal class CreateProductRequestValidator : ProductWriteRequestValidator<CreateProductRequest>;

internal class UpdateProductRequestValidator : ProductWriteRequestValidator<UpdateProductRequest>;
