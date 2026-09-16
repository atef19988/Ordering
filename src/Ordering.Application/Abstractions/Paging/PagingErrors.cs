using Ordering.Domain.Common;

namespace Ordering.Application.Abstractions.Paging;

public static class PagingErrors
{
    /// <summary>
    /// The cursor is not one this query produced: bad encoding, another version, or made under a
    /// different sort or filter. A 400 with <c>code</c>, not a validation error: the fix is to
    /// start again from the first page, not to change a field.
    /// </summary>
    public static readonly Error InvalidCursor = Error.Validation(
        "paging.invalid_cursor",
        "The cursor is invalid or belongs to a different query; request the first page again without a cursor.");
}
