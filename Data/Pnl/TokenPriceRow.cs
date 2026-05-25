namespace TodoApi.Data.Pnl;

public sealed class TokenPriceRow
{
    public string Token { get; set; } = "";
    public double? LatestPriceUsd { get; set; }
}
