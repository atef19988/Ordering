using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Orders;
using Ordering.Domain.Products;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// The write model. Reads go through Dapper (<c>Read/</c>); nothing here is queried for display.
/// </summary>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);
}
