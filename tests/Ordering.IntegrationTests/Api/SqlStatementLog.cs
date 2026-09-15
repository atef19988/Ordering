using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Records every statement EF sends and every commit/rollback, in order, so a test can assert the
/// shape of a transaction (what ran first, what ran last before <c>COMMIT</c>). Registered in the
/// test host as an <see cref="IInterceptor"/>; production hosts never see it.
/// </summary>
public sealed class SqlStatementLog : DbCommandInterceptor, IDbTransactionInterceptor
{
    public const string Commit = "COMMIT";
    public const string Rollback = "ROLLBACK";

    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        _entries.Enqueue(Commit);
        return result;
    }

    public ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        _entries.Enqueue(Commit);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        _entries.Enqueue(Rollback);
        return result;
    }

    public ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        _entries.Enqueue(Rollback);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command) => _entries.Enqueue(command.CommandText.Trim());
}
