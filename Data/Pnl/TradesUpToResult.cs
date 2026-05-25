namespace TodoApi.Data.Pnl;

public sealed record TradesUpToResult(
    IReadOnlyList<TradeRow> Trades,
    bool IsTruncated,
    int Limit);

