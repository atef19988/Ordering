namespace Ordering.Domain.Common;

/// <summary>
/// Money is <c>decimal</c> with two places everywhere (<c>decimal(18,2)</c> in SQL Server).
/// </summary>
public static class Money
{
    public const int Precision = 18;
    public const int Scale = 2;

    /// <summary>Commercial rounding: half away from zero, so 0.125 becomes 0.13.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, Scale, MidpointRounding.AwayFromZero);
}
