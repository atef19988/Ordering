using Ordering.Application.Abstractions.Paging;
using Ordering.Application.Features.Products.GetProducts;

namespace Ordering.Application.Features.Products;

/// <summary>Read side (Dapper, hand-written SQL). Never opens a transaction, never loads entities.</summary>
public interface IProductQueryRepository
{
    /// <summary>
    /// Up to <c>PageSize + 1</c> rows after the seek position, in sort order tie-broken by code;
    /// the count under the same filter when <see cref="ProductPageCriteria.IsFirstPage"/>.
    /// </summary>
    Task<PageRows<ProductDto>> GetPageAsync(ProductPageCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>
/// One page request with its seek position already typed. Exactly one of the <c>After*</c> sort
/// values is set on a non-first page — the one for <see cref="SortField"/> — plus
/// <see cref="AfterCode"/>, which is the tie-breaker for every sort.
/// </summary>
/// <param name="SearchPrefix">Trimmed, unescaped prefix; null = no filter.</param>
/// <param name="SortField">A <see cref="ProductSort"/> field; the repository maps it to a column itself.</param>
public sealed record ProductPageCriteria(
    string? SearchPrefix,
    bool InStock,
    string SortField,
    bool Descending,
    int PageSize)
{
    public string? AfterCode { get; init; }

    public string? AfterName { get; init; }

    public decimal? AfterPrice { get; init; }

    public bool IsFirstPage => AfterCode is null;
}
