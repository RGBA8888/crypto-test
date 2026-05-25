namespace TodoApi.Data.Pnl;

public sealed record TokenTradesAggRow(
    string Token,
    long TradeCount,
    decimal TotalBuyUsd,
    decimal TotalSellUsd,
    decimal BuyAmount,
    decimal SellAmount);

