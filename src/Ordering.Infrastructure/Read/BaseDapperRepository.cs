using Dapper;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Read;

/// <summary>
/// Owns connection lifetime for the Dapper read side. Subclasses only write SQL; they never open a
/// transaction and never touch EF entities. Positional records are materialised through their
/// constructor, so a query's column aliases must match the record's parameters in order.
/// </summary>
public abstract class BaseDapperRepository(IDbConnectionFactory connectionFactory)
{
    protected async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(Command(sql, parameters, cancellationToken));
    }

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<T>(Command(sql, parameters, cancellationToken));
        return rows.AsList();
    }

    /// <summary>Several result sets in one round trip; <paramref name="read"/> consumes them in order.</summary>
    protected async Task<T> QueryMultipleAsync<T>(string sql, object? parameters, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(Command(sql, parameters, cancellationToken));
        return await read(grid);
    }

    protected async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(Command(sql, parameters, cancellationToken));
    }

    private static CommandDefinition Command(string sql, object? parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, cancellationToken: cancellationToken);
}
