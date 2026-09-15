using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ordering.Application.Abstractions.Persistence;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// The only place SQL Server error numbers are read. Two of them become typed exceptions the
/// Application layer understands; everything else propagates untouched (a 500 via the middleware).
/// </summary>
internal static partial class SqlErrors
{
    /// <summary>Violation of PRIMARY KEY / UNIQUE constraint.</summary>
    public const int UniqueConstraintViolation = 2627;

    /// <summary>Cannot insert duplicate key row ... with unique index.</summary>
    public const int UniqueIndexViolation = 2601;

    /// <summary>Lock request time out period exceeded (<c>SET LOCK_TIMEOUT</c>).</summary>
    public const int LockRequestTimeout = 1222;

    /// <summary>
    /// <paramref name="lockedResource"/> is what a lock timeout is reported against: the call site
    /// knows which table its statement waits on, the error message does not say.
    /// </summary>
    public static bool TryTranslate(Exception exception, string lockedResource, [NotNullWhen(true)] out Exception? translated)
    {
        translated = null;

        var sqlException = exception switch
        {
            SqlException direct => direct,
            DbUpdateException { InnerException: SqlException inner } => inner,
            _ => null,
        };

        if (sqlException is null)
        {
            return false;
        }

        switch (sqlException.Number)
        {
            case UniqueConstraintViolation or UniqueIndexViolation:
                translated = new UniqueViolationException(ConstraintNameOf(sqlException), exception);
                return true;

            case LockRequestTimeout:
                translated = new LockTimeoutException(lockedResource, exception);
                return true;

            default:
                return false;
        }
    }

    // 2627: "Violation of PRIMARY KEY constraint 'pk_idempotency_keys'. Cannot insert duplicate key in object 'dbo.idempotency_keys'. ..."
    // 2601: "Cannot insert duplicate key row in object 'dbo.outbox_messages' with unique index 'ux_outbox_order_created'. ..."
    private static string ConstraintNameOf(SqlException exception) =>
        ConstraintName().Match(exception.Message) is { Success: true } match ? match.Groups["name"].Value : string.Empty;

    [GeneratedRegex(@"(?:constraint|index) '(?<name>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ConstraintName();
}
