namespace ProductManagement.Core.Common.Paging;

/// <summary>
/// One page of a collection plus the information a client needs to navigate the rest.
/// </summary>
public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
