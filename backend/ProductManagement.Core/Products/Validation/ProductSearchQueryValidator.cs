using FluentValidation;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products.Validation;

/// <summary>
/// Rules for <c>GET /api/products/search</c>: valid paging, and a non-blank name of at most 200 characters.
/// </summary>
internal class ProductSearchQueryValidator : AbstractValidator<ProductSearchQuery>
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
