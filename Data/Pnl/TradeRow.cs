namespace Crypto.Data.Pnl;

public sealed class TradeRow
{
    public DateTime BlockTime { get; set; }
    public string TxHash { get; set; } = "";
    public string Token { get; set; } = "";
    public string Side { get; set; } = "";
    public double Amount { get; set; }
    public double VolumeUsd { get; set; }
}
