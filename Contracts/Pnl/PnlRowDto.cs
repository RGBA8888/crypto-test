namespace TodoApi.Contracts.Pnl;

public sealed record PnlRowDto(
    string Token,
    decimal RealizedPnlUsd,
    decimal UnrealizedPnlUsd,
    decimal NetBalanceDelta,
    decimal? CloseHoldings,
    decimal TotalBuyUsd,
    decimal TotalSellUsd,
    decimal? LatestPriceUsd,
    long TradeCount,
    PnlDiagnosticsDto Diagnostics);

