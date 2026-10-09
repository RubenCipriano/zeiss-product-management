using FluentValidation;

namespace ProductManagement.Application.Common.Paging;

internal sealed class PageQueryValidator : AbstractValidator<PageQuery>
{
    public PageQueryValidator()
    {
        RuleFor(query => query.Page)
            .InclusiveBetween(1, PageQuery.MaxPage);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, PageQuery.MaxPageSize);
    }
}
