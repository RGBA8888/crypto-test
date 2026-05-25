namespace Crypto.Data.Pnl;

public interface IPnlQueryRepository
{
    Task<IReadOnlyList<string>> GetTokensTradedInRangeAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<TradesUpToResult> GetTradesUpToAsync(string wallet, IReadOnlyList<string> tokens, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<TradesUpToResult> GetTradesInRangeAsync(string wallet, IReadOnlyList<string> tokens, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<TokenTradesAggRow>> GetTradesAggInRangeAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<TokenDeltasRow>> GetWalletDeltasAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<TokenPriceRow>> GetLatestPricesAsync(IReadOnlyList<string> tokens, DateTimeOffset to, CancellationToken ct);
}
