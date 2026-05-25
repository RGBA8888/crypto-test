namespace TodoApi.Data.Pnl;

public sealed record TokenDeltasRow(
    string Token,
    decimal NetBalanceDelta,
    decimal? CloseHoldings);

