using FluentValidation;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products.Validation;

/// <summary>
/// Rules shared by create and update. Limits come from <see cref="ProductRules"/>, the same source the domain
/// model and the database constraints use.
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

internal sealed class CreateProductRequestValidator : ProductWriteRequestValidator<CreateProductRequest>;

internal sealed class UpdateProductRequestValidator : ProductWriteRequestValidator<UpdateProductRequest>;
