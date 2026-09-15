using System.Data.Common;

namespace Ordering.Infrastructure.Persistence;

public interface IDbConnectionFactory
{
    /// <summary>Returns an open connection. Callers own it and must dispose it.</summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);
}
