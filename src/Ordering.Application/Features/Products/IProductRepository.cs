using Ordering.Domain.Products;

namespace Ordering.Application.Features.Products;

/// <summary>Write-side catalogue access, inside the command's transaction.</summary>
public interface IProductRepository
{
    /// <summary>
    /// The catalogue rows for <paramref name="codes"/>, <b>in database code order</b>. That order is
    /// the lock order every writer uses (deduct here, restore in Task 6), which is what keeps
    /// concurrent multi-line orders from deadlocking. Unknown codes are simply absent.
    /// </summary>
    Task<IReadOnlyList<Product>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
}
