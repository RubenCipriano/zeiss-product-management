using FluentValidation;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Contracts;

namespace ProductManagement.Application.Products.Validation;

internal sealed class StockLevelQueryValidator : AbstractValidator<StockLevelQuery>
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
