using FluentValidation;

namespace ProductManagement.Core.Common.Paging;

/// <summary>
/// Checks that <c>page</c> and <c>pageSize</c> are in range. Reused by the other query validators.
/// </summary>
internal class PageQueryValidator : AbstractValidator<PageQuery>
{
    public PageQueryValidator()
    {
        RuleFor(query => query.Page)
            .InclusiveBetween(1, PageQuery.MaxPage);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, PageQuery.MaxPageSize);
    }
}
