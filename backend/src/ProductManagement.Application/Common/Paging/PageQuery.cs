namespace ProductManagement.Application.Common.Paging;

/// <summary>
/// Paging parameters shared by every collection endpoint.
/// </summary>
public record PageQuery
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    /// <summary>1-based page number. Defaults to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page (1-100). Defaults to 20.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    public int Skip => (Page - 1) * PageSize;
}
