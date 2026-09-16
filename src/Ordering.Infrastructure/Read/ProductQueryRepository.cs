using System.Data;
using System.Text;
using Dapper;
using Ordering.Application.Abstractions.Paging;
using Ordering.Application.Features.Products;
using Ordering.Application.Features.Products.GetProducts;
using Ordering.Domain.Common;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Read;

/// <summary>
/// Keyset paging over <c>products</c>. Every page is one index seek — the clustered key for
/// <c>code</c>, <c>ix_products_name (name, code)</c> or <c>ix_products_price (price, code)</c> —
/// plus a key lookup per row; there is no <c>OFFSET</c> anywhere. The SQL text is assembled
/// from the whitelist below and from fixed fragments; request values only ever travel as
/// parameters, and the prefix is escaped for <c>LIKE</c>.
/// </summary>
internal sealed class ProductQueryRepository(IDbConnectionFactory connectionFactory)
    : BaseDapperRepository(connectionFactory), IProductQueryRepository
{
    // Sort field (Application vocabulary) → column. The only interpolation into SQL, ever.
    private static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ProductSort.Code] = "code",
        [ProductSort.Name] = "name",
        [ProductSort.Price] = "price",
    };

    // Aliases = ProductDto constructor parameters, in order.
    private const string SelectPage =
        """
        SELECT TOP (@take)
               code               AS Code,
               name               AS Name,
               price              AS Price,
               available_quantity AS AvailableQuantity
          FROM products
        """;

    private const string SelectCount =
        """
        SELECT COUNT_BIG(*)
          FROM products
        """;

    public Task<PageRows<ProductDto>> GetPageAsync(ProductPageCriteria criteria, CancellationToken cancellationToken)
    {
        var column = SortColumns[criteria.SortField];
        var direction = criteria.Descending ? "DESC" : "ASC";
        var after = criteria.Descending ? "<" : ">";

        // Predicates are added only when they apply, so each shape gets its own sargable plan
        // instead of one plan hedging on `@x IS NULL OR …`.
        var filter = new List<string>();

        if (criteria.SearchPrefix is not null)
        {
            filter.Add(@"(code LIKE @prefix ESCAPE '\' OR name LIKE @prefix ESCAPE '\')");
        }

        if (criteria.InStock)
        {
            // Deliberately unindexed (Task 15): evaluated on the rows the seek returns.
            filter.Add("available_quantity > 0");
        }

        var page = filter.ToList();

        if (!criteria.IsFirstPage)
        {
            // The expanded row-value comparison `(column, code) > (@afterKey, @afterCode)`; T-SQL has no tuple `<`.
            page.Add(column == "code"
                ? $"code {after} @afterCode"
                : $"({column} {after} @afterKey OR ({column} = @afterKey AND code {after} @afterCode))");
        }

        var sql = new StringBuilder()
            .AppendLine(SelectPage)
            .AppendLine(Where(page))
            .AppendLine(column == "code"
                ? $" ORDER BY code {direction};"
                : $" ORDER BY {column} {direction}, code {direction};");

        if (criteria.IsFirstPage)
        {
            // One COUNT_BIG per new query, in the same round trip as its first page.
            sql.AppendLine(SelectCount).AppendLine(Where(filter)).AppendLine(";");
        }

        var parameters = new DynamicParameters();
        parameters.Add("take", criteria.PageSize + 1);

        if (criteria.SearchPrefix is not null)
        {
            parameters.Add("prefix", LikePrefix(criteria.SearchPrefix));
        }

        if (!criteria.IsFirstPage)
        {
            parameters.Add("afterCode", criteria.AfterCode);

            switch (criteria.SortField)
            {
                case ProductSort.Name:
                    parameters.Add("afterKey", criteria.AfterName);
                    break;
                case ProductSort.Price:
                    // Same precision/scale as the column: a wider parameter turns the seek into a scan.
                    parameters.Add("afterKey", criteria.AfterPrice, DbType.Decimal, precision: Money.Precision, scale: Money.Scale);
                    break;
            }
        }

        return QueryMultipleAsync(sql.ToString(), parameters, async grid =>
        {
            var rows = (await grid.ReadAsync<ProductDto>()).AsList();
            var total = criteria.IsFirstPage ? await grid.ReadSingleAsync<long>() : (long?)null;
            return new PageRows<ProductDto>(rows, total);
        }, cancellationToken);
    }

    private static string Where(IReadOnlyList<string> predicates) =>
        predicates.Count == 0 ? string.Empty : " WHERE " + string.Join("\n   AND ", predicates);

    /// <summary>
    /// <c>search%</c> with every LIKE metacharacter escaped, so <c>50%</c> matches the product
    /// named <c>50%_off</c> and nothing else, and <c>[</c> is a bracket, not a character class.
    /// </summary>
    internal static string LikePrefix(string search) =>
        search
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal)
        + "%";
}
