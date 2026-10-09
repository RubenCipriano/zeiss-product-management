using FluentValidation;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products.Validation;

internal sealed class ProductSearchQueryValidator : AbstractValidator<ProductSearchQuery>
{
    public ProductSearchQueryValidator()
    {
        Include(new PageQueryValidator());

        RuleFor(query => query.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("'Name' must not be empty.")
            .MaximumLength(ProductRules.NameMaxLength);
    }
}
