namespace Ordering.Application.Abstractions.Paging;

/// <summary>
/// One page of a keyset-paged list. <see cref="HasMore"/> comes from having fetched one row past
/// <see cref="PageSize"/>, never from a count; <see cref="Total"/> is present on the first page only.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageSize, bool HasMore, string? NextCursor, long? Total)
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;
}

/// <summary>
/// What a read repository hands back for one page request: up to <c>pageSize + 1</c> rows in
/// page order, and the filter's count when the request was a first page. The extra row is the
/// only source of <see cref="Page{T}.HasMore"/>.
/// </summary>
public sealed record PageRows<T>(IReadOnlyList<T> Rows, long? Total)
{
    /// <summary>Drops the look-ahead row and builds the cursor that resumes after the last row kept.</summary>
    public Page<T> ToPage(int pageSize, Func<T, PageCursor> cursorAfter)
    {
        var hasMore = Rows.Count > pageSize;
        var items = hasMore ? Rows.Take(pageSize).ToList() : Rows;
        var nextCursor = hasMore ? cursorAfter(items[^1]).Encode() : null;

        return new Page<T>(items, pageSize, hasMore, nextCursor, Total);
    }
}
