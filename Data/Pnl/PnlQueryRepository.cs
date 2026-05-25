using System.Data;
using ClickHouse.Driver.ADO;
using Dapper;
using TodoApi.Services.ClickHouse;

namespace TodoApi.Data.Pnl;

public sealed class PnlQueryRepository(IConfiguration configuration) : IPnlQueryRepository
{
    private ClickHouseConnection CreateConnection()
    {
        var opts = new ClickHouseOptions();
        configuration.GetSection("ClickHouse").Bind(opts);

        var cs = configuration["CLICKHOUSE_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(cs))
        {
            return new ClickHouseConnection(cs);
        }

        // Minimal env-based builder to avoid leaking secrets into appsettings.
        var host = configuration["CLICKHOUSE_HOST"] ?? "";
        var port = ushort.TryParse(configuration["CLICKHOUSE_PORT"], out var p) ? p : (ushort)8123;
        var username = configuration["CLICKHOUSE_USER"] ?? "";
        var password = configuration["CLICKHOUSE_PASSWORD"] ?? "";
        var database = configuration["CLICKHOUSE_DATABASE"] ?? "__default__";
        var protocol = configuration["CLICKHOUSE_PROTOCOL"] ?? "http";

        // ClickHouseConnectionStringBuilder exists in this driver version; use its property names.
        var builder = new ClickHouse.Driver.ADO.ClickHouseConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Database = database,
            Protocol = protocol,
        };

        return new ClickHouseConnection(builder.ConnectionString);
    }

    public async Task<IReadOnlyList<string>> GetTokensTradedInRangeAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const string sql = """
                           SELECT DISTINCT token
                           FROM ui_trades
                           WHERE user_wallet = @wallet
                             AND block_time >= @from
                             AND block_time <  @to
                           """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new { wallet, from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct, commandTimeout: 60));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TradeRow>> GetTradesUpToAsync(string wallet, IReadOnlyList<string> tokens, DateTimeOffset to, CancellationToken ct)
    {
        // NOTE: Dapper expands IN @tokens into parameters for most ADO providers.
        const string sql = """
                           SELECT
                             block_time AS BlockTime,
                             token      AS Token,
                             side       AS Side,
                             amount     AS Amount,
                             volume_usd AS VolumeUsd
                           FROM ui_trades
                           WHERE user_wallet = @wallet
                             AND token IN @tokens
                             AND block_time < @to
                           ORDER BY block_time ASC
                           LIMIT 200000
                           """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<TradeRow>(new CommandDefinition(sql, new { wallet, tokens, to = to.UtcDateTime }, cancellationToken: ct, commandTimeout: 60));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TokenTradesAggRow>> GetTradesAggInRangeAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const string sql = """
                           SELECT
                             token AS Token,
                             count() AS TradeCount,
                             sumIf(volume_usd, side = 'Buy')  AS TotalBuyUsd,
                             sumIf(volume_usd, side = 'Sell') AS TotalSellUsd,
                             sumIf(amount, side = 'Buy')  AS BuyAmount,
                             sumIf(amount, side = 'Sell') AS SellAmount
                           FROM ui_trades
                           WHERE user_wallet = @wallet
                             AND block_time >= @from
                             AND block_time <  @to
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<TokenTradesAggRow>(new CommandDefinition(sql, new { wallet, from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct, commandTimeout: 60));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TokenDeltasRow>> GetWalletDeltasAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const string sql = """
                           SELECT
                             token AS Token,
                             sum(delta_balance) AS NetBalanceDelta,
                             argMaxMerge(close_holdings) AS CloseHoldings
                           FROM wallet_1m_deltas
                           WHERE user_wallet = @wallet
                             AND minute >= toDateTime(@from)
                             AND minute <  toDateTime(@to)
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<TokenDeltasRow>(new CommandDefinition(sql, new { wallet, from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct, commandTimeout: 60));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TokenPriceRow>> GetLatestPricesAsync(IReadOnlyList<string> tokens, DateTimeOffset to, CancellationToken ct)
    {
        // Safer pattern: compute minute-level price then argMax by minute.
        const string sql = """
                           SELECT
                             token AS Token,
                             argMax(price, minute) AS LatestPriceUsd
                           FROM
                           (
                             SELECT
                               token,
                               minute,
                               argMaxMerge(close) AS price
                             FROM token_prices_1m
                             WHERE token IN @tokens
                               AND minute <= toDateTime(@to)
                             GROUP BY token, minute
                           )
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<TokenPriceRow>(new CommandDefinition(sql, new { tokens, to = to.UtcDateTime }, cancellationToken: ct, commandTimeout: 60));
        return rows.AsList();
    }
}
