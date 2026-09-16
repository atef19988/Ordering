using System.Globalization;
using FluentValidation;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Paging;

namespace Ordering.Application.Features.Products.GetProducts;

public sealed record ProductDto(string Code, string Name, decimal Price, int AvailableQuantity);

/// <summary>
/// One keyset page of the catalogue. There is no way to ask for the whole table.
/// </summary>
/// <param name="Search">Trimmed prefix of <c>code</c> or <c>name</c>, case-insensitive; null or blank = no filter.</param>
/// <param name="InStock">When true only rows with <c>available_quantity &gt; 0</c>.</param>
/// <param name="Sort"><c>code</c> | <c>name</c> | <c>price</c>, optionally <c>:desc</c>.</param>
/// <param name="PageSize">1 … 200.</param>
/// <param name="Cursor">The previous page's <c>nextCursor</c>; null = first page (which carries <c>total</c>).</param>
public sealed record GetProductsQuery(string? Search, bool InStock, string Sort, int PageSize, string? Cursor)
    : IQuery<Page<ProductDto>>
{
    public const string DefaultSort = ProductSort.Code;
    public const int SearchMaxLength = 64;
}

/// <summary>
/// The sort whitelist. A field name is the only thing the read repository ever interpolates into
/// SQL, and only after mapping it through its own column table.
/// </summary>
public static class ProductSort
{
    public const string Code = "code";
    public const string Name = "name";
    public const string Price = "price";

    private const string DescendingSuffix = ":desc";

    public static readonly IReadOnlyList<string> Fields = [Code, Name, Price];

    public static bool TryParse(string? sort, out string field, out bool descending)
    {
        var value = (sort ?? string.Empty).Trim();
        descending = value.EndsWith(DescendingSuffix, StringComparison.OrdinalIgnoreCase);
        var name = descending ? value[..^DescendingSuffix.Length] : value;

        field = Fields.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return field.Length > 0;
    }
}

internal sealed class GetProductsQueryValidator : AbstractValidator<GetProductsQuery>
{
    public GetProductsQueryValidator()
    {
        // Property names follow the query string, because that is what the caller has to fix.
        RuleFor(q => q.Search)
            .Must(search => search!.Trim().Length <= GetProductsQuery.SearchMaxLength)
            .When(q => q.Search is not null)
            .WithMessage($"search must be at most {GetProductsQuery.SearchMaxLength} characters.")
            .OverridePropertyName("search");

        RuleFor(q => q.Sort)
            .Must(sort => ProductSort.TryParse(sort, out _, out _))
            .WithMessage($"sort must be one of {string.Join(", ", ProductSort.Fields)}, optionally followed by ':desc'.")
            .OverridePropertyName("sort");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(Page<ProductDto>.MinPageSize, Page<ProductDto>.MaxPageSize)
            .WithMessage($"pageSize must be between {Page<ProductDto>.MinPageSize} and {Page<ProductDto>.MaxPageSize}.")
            .OverridePropertyName("pageSize");
    }
}

/// <summary>
/// Turns the request into typed seek values for the read side and the last row back into a
/// cursor. A cursor is accepted only if it was made under the same sort and the same filter.
/// </summary>
internal sealed class GetProductsQueryHandler(IProductQueryRepository products)
    : IQueryHandler<GetProductsQuery, Page<ProductDto>>
{
    public async Task<Result<Page<ProductDto>>> Handle(GetProductsQuery query, CancellationToken cancellationToken)
    {
        ProductSort.TryParse(query.Sort, out var field, out var descending);
        var direction = PageCursor.DirectionOf(descending);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var filterHash = PageCursor.HashFilter(search, query.InStock ? "true" : "false");

        var criteria = new ProductPageCriteria(search, query.InStock, field, descending, query.PageSize);

        if (query.Cursor is not null)
        {
            if (!PageCursor.TryDecode(query.Cursor, out var cursor)
                || !cursor.Matches(field, direction, filterHash)
                || !TrySeekAfter(criteria, cursor, out criteria))
            {
                return PagingErrors.InvalidCursor;
            }
        }

        var rows = await products.GetPageAsync(criteria, cancellationToken);

        return rows.ToPage(query.PageSize, last => new PageCursor(field, direction, SortValueOf(last, field), last.Code, filterHash));
    }

    /// <summary>The cursor's key must parse as the sort column's type; a price that does not is a forged cursor.</summary>
    private static bool TrySeekAfter(ProductPageCriteria first, PageCursor cursor, out ProductPageCriteria seek)
    {
        seek = first;

        switch (first.SortField)
        {
            case ProductSort.Price:
                if (!decimal.TryParse(cursor.Key, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                {
                    return false;
                }

                seek = first with { AfterCode = cursor.TieBreaker, AfterPrice = price };
                return true;

            case ProductSort.Name:
                seek = first with { AfterCode = cursor.TieBreaker, AfterName = cursor.Key };
                return true;

            default:
                seek = first with { AfterCode = cursor.TieBreaker };
                return true;
        }
    }

    private static string SortValueOf(ProductDto row, string field) => field switch
    {
        ProductSort.Name => row.Name,
        ProductSort.Price => row.Price.ToString(CultureInfo.InvariantCulture),
        _ => row.Code,
    };
}
