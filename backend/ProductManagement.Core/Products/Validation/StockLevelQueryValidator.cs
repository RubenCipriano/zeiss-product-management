using FluentValidation;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products.Validation;

/// <summary>
/// Rules for <c>GET /api/products/stock-level</c>: valid paging; <c>min</c> and <c>max</c> are optional, not
/// negative, and <c>max</c> is not below <c>min</c>.
/// </summary>
internal class StockLevelQueryValidator : AbstractValidator<StockLevelQuery>
{
    public StockLevelQueryValidator()
    {
        Include(new PageQueryValidator());

        RuleFor(query => query.Min)
            .GreaterThanOrEqualTo(0)
            .When(query => query.Min.HasValue);

        RuleFor(query => query.Max)
            .GreaterThanOrEqualTo(0)
            .When(query => query.Max.HasValue);

        RuleFor(query => query.Max)
            .GreaterThanOrEqualTo(query => query.Min)
            .When(query => query.Min.HasValue && query.Max.HasValue)
            .WithMessage("'Max' must be greater than or equal to 'Min'.");
    }
}
