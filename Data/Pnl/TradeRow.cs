namespace TodoApi.Data.Pnl;

public sealed record TradeRow(
    DateTimeOffset BlockTime,
    string Token,
    string Side,
    decimal Amount,
    decimal VolumeUsd);

