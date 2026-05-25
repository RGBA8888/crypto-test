using System.Data;
using ClickHouse.Driver.ADO;
using Dapper;
using TodoApi.Services.ClickHouse;

namespace TodoApi.Data.Pnl;

public sealed class PnlQueryRepository(IConfiguration configuration, ILogger<PnlQueryRepository> logger) : IPnlQueryRepository
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
        // The public dataset tables live in the `solanav1` database.
        var database = configuration["CLICKHOUSE_DATABASE"] ?? "solanav1";
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

    private int GetCommandTimeoutSeconds()
    {
        return int.TryParse(configuration["CLICKHOUSE_QUERY_TIMEOUT_SECONDS"], out var t)
            ? Math.Clamp(t, 1, 300)
            : 60;
    }

    private static long ElapsedMs(System.Diagnostics.Stopwatch sw) => sw.ElapsedMilliseconds;

    public async Task<IReadOnlyList<string>> GetTokensTradedInRangeAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const string sql = """
                           SELECT DISTINCT token
                           FROM solanav1.ui_trades
                           WHERE user_wallet = @wallet
                             AND block_time >= @from
                             AND block_time <  @to
                           """;

        await using var conn = CreateConnection();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            sql,
            new { wallet, from = from.UtcDateTime, to = to.UtcDateTime },
            cancellationToken: ct,
            commandTimeout: GetCommandTimeoutSeconds()));
        sw.Stop();
        var list = rows.AsList();
        logger.LogInformation(
            "CH GetTokensTradedInRange wallet={Wallet} from={From} to={To} -> {Count} tokens in {ElapsedMs}ms",
            wallet,
            from,
            to,
            list.Count,
            ElapsedMs(sw));
        return list;
    }

    public async Task<TradesUpToResult> GetTradesUpToAsync(string wallet, IReadOnlyList<string> tokens, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Keep a conservative default to avoid large responses / timeouts.
        // Can be increased via env var CLICKHOUSE_TRADES_UP_TO_LIMIT when needed.
        var limit = int.TryParse(configuration["CLICKHOUSE_TRADES_UP_TO_LIMIT"], out var l) ? l : 50_000;
        limit = Math.Clamp(limit, 10_000, 500_000);
        var pageSize = int.TryParse(configuration["CLICKHOUSE_TRADES_PAGE_SIZE"], out var ps) ? ps : 5_000;
        pageSize = Math.Clamp(pageSize, 500, 20_000);

        // Dataset is stable only for last few days; do not attempt unbounded warmup scans.
        var warmupDays = int.TryParse(configuration["CLICKHOUSE_WARMUP_MAX_DAYS"], out var d) ? d : 7;
        warmupDays = Math.Clamp(warmupDays, 1, 30);
        var warmupFrom = from.UtcDateTime.AddDays(-warmupDays);

        // NOTE: Dapper expands IN @tokens into parameters for most ADO providers.
        // Fetch in pages to avoid large HTTP responses being cut off.
        // NOTE: This pagination uses (block_time, tx_hash). Some ClickHouse table orderings may make tuple cursor
        // slower than desired; keep limits conservative and rely on RangeOnly for fast paths.
        const string sql = """
                           SELECT
                             block_time AS BlockTime,
                             tx_hash    AS TxHash,
                             token      AS Token,
                             side       AS Side,
                             amount     AS Amount,
                             volume_usd AS VolumeUsd
                           FROM solanav1.ui_trades
                           WHERE user_wallet = @wallet
                             AND token IN @tokens
                             AND block_time >= @warmupFrom
                             AND (block_time, tx_hash) > (@cursorTime, @cursorTx)
                             AND block_time < @to
                           ORDER BY block_time ASC, tx_hash ASC
                           LIMIT @pageSize
                           """;

        await using var conn = CreateConnection();
        var all = new List<TradeRow>(Math.Min(limit, 50_000));

        var cursorTime = warmupFrom;
        var cursorTx = "";

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        while (all.Count < limit)
        {
            var remaining = limit - all.Count;
            var take = Math.Min(pageSize, remaining);

            var swPage = System.Diagnostics.Stopwatch.StartNew();
            var page = (await conn.QueryAsync<TradeRow>(new CommandDefinition(
                sql,
                new
                {
                    wallet,
                    tokens,
                    warmupFrom,
                    cursorTime,
                    cursorTx,
                    to = to.UtcDateTime,
                    pageSize = take
                },
                cancellationToken: ct,
                commandTimeout: GetCommandTimeoutSeconds()))).AsList();
            swPage.Stop();

            if (page.Count == 0)
            {
                swTotal.Stop();
                logger.LogInformation(
                    "CH GetTradesUpTo wallet={Wallet} warmupFrom={WarmupFrom} to={To} tokens={TokenCount} -> {TradeCount} trades, truncated={Truncated}, limit={Limit} in {ElapsedMs}ms",
                    wallet,
                    warmupFrom,
                    to,
                    tokens.Count,
                    all.Count,
                    false,
                    limit,
                    ElapsedMs(swTotal));
                return new TradesUpToResult(all, false, limit);
            }

            all.AddRange(page);

            var last = page[^1];
            cursorTime = last.BlockTime;
            cursorTx = last.TxHash ?? "";

            // Safety: if tx_hash is empty, break to avoid infinite loop (should not happen in this dataset).
            if (page.Count < take)
            {
                swTotal.Stop();
                logger.LogInformation(
                    "CH GetTradesUpTo wallet={Wallet} warmupFrom={WarmupFrom} to={To} tokens={TokenCount} -> {TradeCount} trades, truncated={Truncated}, limit={Limit} in {ElapsedMs}ms",
                    wallet,
                    warmupFrom,
                    to,
                    tokens.Count,
                    all.Count,
                    false,
                    limit,
                    ElapsedMs(swTotal));
                return new TradesUpToResult(all, false, limit);
            }

            logger.LogDebug(
                "CH GetTradesUpTo page wallet={Wallet} -> +{PageCount} trades (total={Total}) in {ElapsedMs}ms",
                wallet,
                page.Count,
                all.Count,
                ElapsedMs(swPage));
        }

        swTotal.Stop();
        logger.LogWarning(
            "CH GetTradesUpTo wallet={Wallet} warmupFrom={WarmupFrom} to={To} tokens={TokenCount} -> hit limit {Limit} (returned {TradeCount}) in {ElapsedMs}ms",
            wallet,
            warmupFrom,
            to,
            tokens.Count,
            limit,
            all.Count,
            ElapsedMs(swTotal));
        return new TradesUpToResult(all, true, limit);
    }

    public async Task<TradesUpToResult> GetTradesInRangeAsync(string wallet, IReadOnlyList<string> tokens, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Range-only mode must be fast: restrict by time to avoid scanning wallet history.
        var limit = int.TryParse(configuration["CLICKHOUSE_TRADES_IN_RANGE_LIMIT"], out var l) ? l : 200_000;
        limit = Math.Clamp(limit, 10_000, 500_000);
        var pageSize = int.TryParse(configuration["CLICKHOUSE_TRADES_PAGE_SIZE"], out var ps) ? ps : 5_000;
        pageSize = Math.Clamp(pageSize, 500, 20_000);

        const string sql = """
                           SELECT
                             block_time AS BlockTime,
                             tx_hash    AS TxHash,
                             token      AS Token,
                             side       AS Side,
                             amount     AS Amount,
                             volume_usd AS VolumeUsd
                           FROM solanav1.ui_trades
                           WHERE user_wallet = @wallet
                             AND token IN @tokens
                             AND block_time >= @from
                             AND (block_time, tx_hash) > (@cursorTime, @cursorTx)
                             AND block_time < @to
                           ORDER BY block_time ASC, tx_hash ASC
                           LIMIT @pageSize
                           """;

        await using var conn = CreateConnection();
        var all = new List<TradeRow>(Math.Min(limit, 50_000));

        var cursorTime = from.UtcDateTime;
        var cursorTx = "";

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        while (all.Count < limit)
        {
            var remaining = limit - all.Count;
            var take = Math.Min(pageSize, remaining);

            var page = (await conn.QueryAsync<TradeRow>(new CommandDefinition(
                sql,
                new
                {
                    wallet,
                    tokens,
                    from = from.UtcDateTime,
                    cursorTime,
                    cursorTx,
                    to = to.UtcDateTime,
                    pageSize = take
                },
                cancellationToken: ct,
                commandTimeout: GetCommandTimeoutSeconds()))).AsList();

            if (page.Count == 0)
            {
                swTotal.Stop();
                logger.LogInformation(
                    "CH GetTradesInRange wallet={Wallet} from={From} to={To} tokens={TokenCount} -> {TradeCount} trades, truncated={Truncated}, limit={Limit} in {ElapsedMs}ms",
                    wallet,
                    from,
                    to,
                    tokens.Count,
                    all.Count,
                    false,
                    limit,
                    ElapsedMs(swTotal));
                return new TradesUpToResult(all, false, limit);
            }

            all.AddRange(page);

            var last = page[^1];
            cursorTime = last.BlockTime;
            cursorTx = last.TxHash ?? "";

            if (page.Count < take)
            {
                swTotal.Stop();
                logger.LogInformation(
                    "CH GetTradesInRange wallet={Wallet} from={From} to={To} tokens={TokenCount} -> {TradeCount} trades, truncated={Truncated}, limit={Limit} in {ElapsedMs}ms",
                    wallet,
                    from,
                    to,
                    tokens.Count,
                    all.Count,
                    false,
                    limit,
                    ElapsedMs(swTotal));
                return new TradesUpToResult(all, false, limit);
            }
        }

        swTotal.Stop();
        logger.LogWarning(
            "CH GetTradesInRange wallet={Wallet} from={From} to={To} tokens={TokenCount} -> hit limit {Limit} (returned {TradeCount}) in {ElapsedMs}ms",
            wallet,
            from,
            to,
            tokens.Count,
            limit,
            all.Count,
            ElapsedMs(swTotal));
        return new TradesUpToResult(all, true, limit);
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
                             sumIf(amount, side = 'Sell') AS SellAmount,
                             sum(volume_usd * compound_fees) AS EstimatedCompoundFeesUsd
                           FROM solanav1.ui_trades
                           WHERE user_wallet = @wallet
                             AND block_time >= @from
                             AND block_time <  @to
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await conn.QueryAsync<TokenTradesAggRow>(new CommandDefinition(
            sql,
            new { wallet, from = from.UtcDateTime, to = to.UtcDateTime },
            cancellationToken: ct,
            commandTimeout: GetCommandTimeoutSeconds()));
        sw.Stop();
        var list = rows.AsList();
        logger.LogInformation(
            "CH GetTradesAggInRange wallet={Wallet} from={From} to={To} -> {Count} rows in {ElapsedMs}ms",
            wallet,
            from,
            to,
            list.Count,
            ElapsedMs(sw));
        return list;
    }

    public async Task<IReadOnlyList<TokenDeltasRow>> GetWalletDeltasAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const string sql = """
                           SELECT
                             token AS Token,
                             sum(delta_balance) AS NetBalanceDelta,
                             argMaxMerge(close_holdings) AS CloseHoldings
                           FROM solanav1.wallet_1m_deltas
                           WHERE user_wallet = @wallet
                             AND minute >= toDateTime(@from)
                             AND minute <  toDateTime(@to)
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await conn.QueryAsync<TokenDeltasRow>(new CommandDefinition(
            sql,
            new { wallet, from = from.UtcDateTime, to = to.UtcDateTime },
            cancellationToken: ct,
            commandTimeout: GetCommandTimeoutSeconds()));
        sw.Stop();
        var list = rows.AsList();
        logger.LogInformation(
            "CH GetWalletDeltas wallet={Wallet} from={From} to={To} -> {Count} rows in {ElapsedMs}ms",
            wallet,
            from,
            to,
            list.Count,
            ElapsedMs(sw));
        return list;
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
                               mint AS token,
                               minute,
                               argMaxMerge(close) AS price
                             FROM solanav1.token_prices_1m
                             WHERE mint IN @tokens
                               AND minute <= toDateTime(@to)
                             GROUP BY token, minute
                           )
                           GROUP BY token
                           """;

        await using var conn = CreateConnection();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await conn.QueryAsync<TokenPriceRow>(new CommandDefinition(
            sql,
            new { tokens, to = to.UtcDateTime },
            cancellationToken: ct,
            commandTimeout: GetCommandTimeoutSeconds()));
        sw.Stop();
        var list = rows.AsList();
        logger.LogInformation(
            "CH GetLatestPrices to={To} tokens={TokenCount} -> {Count} rows in {ElapsedMs}ms",
            to,
            tokens.Count,
            list.Count,
            ElapsedMs(sw));
        return list;
    }
}
