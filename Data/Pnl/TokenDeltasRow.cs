namespace Crypto.Data.Pnl;

public sealed class TokenDeltasRow
{
    public string Token { get; set; } = "";
    public double NetBalanceDelta { get; set; }
    public double? CloseHoldings { get; set; }
}
