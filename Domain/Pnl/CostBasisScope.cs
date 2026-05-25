namespace Crypto.Domain.Pnl;

/// <summary>
/// Controls how cost basis is computed for realized/unrealized PnL.
/// </summary>
public enum CostBasisScope
{
    /// <summary>
    /// Use only trades within the requested time range. Fast, but may be inaccurate if the position was opened earlier.
    /// </summary>
    RangeOnly = 0,
    /// <summary>
    /// Attempt to include earlier trades (warmup history) to approximate a full wallet cost basis up to <c>to</c>.
    /// </summary>
    Warmup = 1
}
