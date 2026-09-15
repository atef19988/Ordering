using Dapper;
using Ordering.Domain.Products;
using Ordering.Infrastructure.Persistence;

namespace Ordering.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public class UnitOfWorkTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Rollback_discards_changes_saved_inside_the_transaction()
    {
        await using var context = fixture.CreateContext();
        var unitOfWork = new UnitOfWork(context);

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        context.Products.Add(Product.Create("UOW-ROLLBACK", "probe", 1m, 1));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        await unitOfWork.RollbackAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync("UOW-ROLLBACK"));
    }

    [Fact]
    public async Task Commit_makes_changes_visible()
    {
        await using var context = fixture.CreateContext();
        var unitOfWork = new UnitOfWork(context);

        try
        {
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            context.Products.Add(Product.Create("UOW-COMMIT", "probe", 1m, 1));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            await unitOfWork.CommitAsync(CancellationToken.None);

            Assert.Equal(1, await CountAsync("UOW-COMMIT"));
        }
        finally
        {
            await using var connection = await fixture.OpenConnectionAsync();
            await connection.ExecuteAsync("DELETE FROM products WHERE code = 'UOW-COMMIT'");
        }
    }

    [Fact]
    public async Task Commit_and_rollback_without_a_transaction_are_no_ops()
    {
        await using var context = fixture.CreateContext();
        var unitOfWork = new UnitOfWork(context);

        await unitOfWork.CommitAsync(CancellationToken.None);
        await unitOfWork.RollbackAsync(CancellationToken.None);
    }

    private async Task<int> CountAsync(string code)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE code = @code", new { code });
    }
}
