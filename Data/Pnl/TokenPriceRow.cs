namespace TodoApi.Data.Pnl;

public sealed record TokenPriceRow(
    string Token,
    decimal? LatestPriceUsd);

