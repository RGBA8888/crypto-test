namespace TodoApi.Data.Pnl;

// NOTE: ClickHouse aggregates are typically returned as UInt64/Double by the driver.
// Use a mutable POCO so Dapper can materialize without requiring an exact constructor match.
public sealed class TokenTradesAggRow
{
    public string Token { get; set; } = "";
    public ulong TradeCount { get; set; }
    public double TotalBuyUsd { get; set; }
    public double TotalSellUsd { get; set; }
    public double BuyAmount { get; set; }
    public double SellAmount { get; set; }
    public double? EstimatedCompoundFeesUsd { get; set; }
}
