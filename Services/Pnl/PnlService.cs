using TodoApi.Contracts.Pnl;
using TodoApi.Data.Pnl;
using TodoApi.Domain.Pnl;

namespace TodoApi.Services.Pnl;

public sealed class PnlService(IPnlQueryRepository repository, IConfiguration configuration, ILogger<PnlService> logger) : IPnlService
{
    public async Task<PnlResponseDto> GetWalletPnlAsync(
        string wallet,
        DateTimeOffset from,
        DateTimeOffset to,
        CostBasisScope scope,
        bool includeTransfers,
        CancellationToken ct)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "PnL start wallet={Wallet} from={From} to={To} scope={Scope} includeTransfers={IncludeTransfers}",
            wallet,
            from,
            to,
            scope,
            includeTransfers);

        // Fetch tokens traded in range
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tokens = await repository.GetTokensTradedInRangeAsync(wallet, from, to, ct);
        sw.Stop();
        logger.LogInformation("PnL tokens={TokenCount} fetched in {ElapsedMs}ms", tokens.Count, sw.ElapsedMilliseconds);

        if (tokens.Count == 0)
        {
            var emptyMeta = new PnlMetaDto(
                Wallet: wallet,
                From: from,
                To: to,
                CostBasisMethod: "weighted_average",
                CostBasisScope: scope == CostBasisScope.Warmup ? "warmup" : "range_only",
                IncludeTransfers: includeTransfers,
                TradesUpToLimit: 0,
                TradesUpToTruncated: false,
                FeesModel: "diagnostic_assuming_compound_fees_is_fraction");
            return new PnlResponseDto(emptyMeta, Array.Empty<PnlRowDto>());
        }

        // Aggregates in range (diagnostics)
        sw.Restart();
        var aggRows = await repository.GetTradesAggInRangeAsync(wallet, from, to, ct);
        sw.Stop();
        logger.LogInformation("PnL aggRows={RowCount} fetched in {ElapsedMs}ms", aggRows.Count, sw.ElapsedMilliseconds);
        var aggByToken = aggRows.ToDictionary(x => x.Token, StringComparer.Ordinal);

        // Deltas and holdings
        sw.Restart();
        var deltas = await repository.GetWalletDeltasAsync(wallet, from, to, ct);
        sw.Stop();
        logger.LogInformation("PnL deltas={RowCount} fetched in {ElapsedMs}ms", deltas.Count, sw.ElapsedMilliseconds);
        var deltasByToken = deltas.ToDictionary(x => x.Token, StringComparer.Ordinal);

        // Prices
        sw.Restart();
        var prices = await repository.GetLatestPricesAsync(tokens, to, ct);
        sw.Stop();
        logger.LogInformation("PnL prices={RowCount} fetched in {ElapsedMs}ms", prices.Count, sw.ElapsedMilliseconds);
        var priceByToken = prices.ToDictionary(x => x.Token, StringComparer.Ordinal);

        TradesUpToResult tradesResult;
        var scopeUsed = scope;
        if (scope == CostBasisScope.Warmup)
        {
            try
            {
                // Trades up to 'to' to compute warmup + in-range realized/remaining basis
                var warmupTimeoutSeconds = int.TryParse(configuration["CLICKHOUSE_WARMUP_TIMEOUT_SECONDS"], out var ts) ? ts : 10;
                warmupTimeoutSeconds = Math.Clamp(warmupTimeoutSeconds, 1, 120);
                using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                warmupCts.CancelAfter(TimeSpan.FromSeconds(warmupTimeoutSeconds));

                sw.Restart();
                var warmupTask = repository.GetTradesUpToAsync(wallet, tokens, from, to, warmupCts.Token);
                var completed = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(warmupTimeoutSeconds), ct));
                if (completed == warmupTask)
                {
                    tradesResult = await warmupTask;
                }
                else
                {
                    // Don't block the request thread on a potentially stuck driver read.
                    _ = warmupTask.ContinueWith(
                        t =>
                        {
                            if (t.IsFaulted)
                            {
                                logger.LogWarning(t.Exception, "PnL warmup background task faulted after timeout");
                            }
                            else if (t.IsCanceled)
                            {
                                logger.LogInformation("PnL warmup background task canceled after timeout");
                            }
                            else
                            {
                                logger.LogInformation("PnL warmup background task finished after timeout");
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    throw new TimeoutException($"Warmup trades query exceeded {warmupTimeoutSeconds}s");
                }
                sw.Stop();
                logger.LogInformation(
                    "PnL warmup trades={TradeCount} fetched in {ElapsedMs}ms (truncated={Truncated})",
                    tradesResult.Trades.Count,
                    sw.ElapsedMilliseconds,
                    tradesResult.IsTruncated);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Warmup can be expensive or flaky in some environments; keep API usable by falling back.
                logger.LogWarning(ex, "PnL warmup fetch timed out/canceled; falling back to range-only trades");
                scopeUsed = CostBasisScope.RangeOnly;
                sw.Restart();
                tradesResult = await repository.GetTradesInRangeAsync(wallet, tokens, from, to, ct);
                sw.Stop();
                logger.LogInformation(
                    "PnL fallback range trades={TradeCount} fetched in {ElapsedMs}ms (truncated={Truncated})",
                    tradesResult.Trades.Count,
                    sw.ElapsedMilliseconds,
                    tradesResult.IsTruncated);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "PnL warmup fetch failed; falling back to range-only trades");
                scopeUsed = CostBasisScope.RangeOnly;
                sw.Restart();
                tradesResult = await repository.GetTradesInRangeAsync(wallet, tokens, from, to, ct);
                sw.Stop();
                logger.LogInformation(
                    "PnL fallback range trades={TradeCount} fetched in {ElapsedMs}ms (truncated={Truncated})",
                    tradesResult.Trades.Count,
                    sw.ElapsedMilliseconds,
                    tradesResult.IsTruncated);
            }
        }
        else
        {
            // Range-only mode: do not scan wallet history.
            sw.Restart();
            tradesResult = await repository.GetTradesInRangeAsync(wallet, tokens, from, to, ct);
            sw.Stop();
            logger.LogInformation("PnL range trades={TradeCount} fetched in {ElapsedMs}ms (truncated={Truncated})", tradesResult.Trades.Count, sw.ElapsedMilliseconds, tradesResult.IsTruncated);
        }

        if (tradesResult.IsTruncated)
        {
            logger.LogWarning(
                "Trades list was truncated (scope={Scope}, limit={Limit}) for wallet={Wallet}, from={From}, to={To}",
                scope,
                tradesResult.Limit,
                wallet,
                from,
                to);
        }

        var tradesUpTo = tradesResult.Trades;

        var meta = new PnlMetaDto(
            Wallet: wallet,
            From: from,
            To: to,
            CostBasisMethod: "weighted_average",
            CostBasisScope: scopeUsed == CostBasisScope.Warmup ? "warmup" : "range_only",
            IncludeTransfers: includeTransfers,
            TradesUpToLimit: tradesResult.Limit,
            TradesUpToTruncated: tradesResult.IsTruncated,
            FeesModel: "diagnostic_assuming_compound_fees_is_fraction");

        var stateByToken = tokens.ToDictionary(t => t, _ => new WeightedAverageCostState(), StringComparer.Ordinal);

        foreach (var trade in tradesUpTo)
        {
            if (!stateByToken.TryGetValue(trade.Token, out var state))
                continue;

            // Apply warmup trades only if scope is Warmup; always apply in-range trades.
            var tradeTime = new DateTimeOffset(trade.BlockTime, TimeSpan.Zero);
            var inRange = tradeTime >= from && tradeTime < to;
            if (!inRange && scope != CostBasisScope.Warmup)
                continue;

            if (string.Equals(trade.Side, "Buy", StringComparison.OrdinalIgnoreCase))
            {
                state.Buy(Convert.ToDecimal(trade.Amount), Convert.ToDecimal(trade.VolumeUsd));
            }
            else if (string.Equals(trade.Side, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                state.Sell(Convert.ToDecimal(trade.Amount), Convert.ToDecimal(trade.VolumeUsd));
            }
        }

        var rows = new List<PnlRowDto>(tokens.Count);

        foreach (var token in tokens)
        {
            aggByToken.TryGetValue(token, out var agg);
            deltasByToken.TryGetValue(token, out var delta);
            priceByToken.TryGetValue(token, out var price);
            var state = stateByToken[token];

            var buyAmount = agg is null ? 0m : Convert.ToDecimal(agg.BuyAmount);
            var sellAmount = agg is null ? 0m : Convert.ToDecimal(agg.SellAmount);
            var tradeNetDelta = buyAmount - sellAmount;

            var netBalanceDelta = delta is null ? 0m : Convert.ToDecimal(delta.NetBalanceDelta);
            var closeHoldings = delta?.CloseHoldings is null ? (decimal?)null : Convert.ToDecimal(delta.CloseHoldings.Value);

            var externalTransferSuspected =
                Math.Abs((double)(netBalanceDelta - tradeNetDelta)) > 1e-9;

            var latestPriceUsd = price?.LatestPriceUsd is null ? (decimal?)null : Convert.ToDecimal(price.LatestPriceUsd.Value);
            var unrealizedPnl = 0m;
            if (closeHoldings is not null && latestPriceUsd is not null)
            {
                unrealizedPnl = closeHoldings.Value * latestPriceUsd.Value - state.CostBasisUsd;
            }

            rows.Add(new PnlRowDto(
                Token: token,
                RealizedPnlUsd: state.RealizedPnlUsd,
                UnrealizedPnlUsd: unrealizedPnl,
                NetBalanceDelta: netBalanceDelta,
                CloseHoldings: closeHoldings,
                TotalBuyUsd: agg is null ? 0m : Convert.ToDecimal(agg.TotalBuyUsd),
                TotalSellUsd: agg is null ? 0m : Convert.ToDecimal(agg.TotalSellUsd),
                LatestPriceUsd: latestPriceUsd,
                TradeCount: agg is null ? 0 : (long)Math.Min((ulong)long.MaxValue, agg.TradeCount),
                Diagnostics: new PnlDiagnosticsDto(
                    BuyAmount: buyAmount,
                    SellAmount: sellAmount,
                    TradeNetDelta: tradeNetDelta,
                    ExternalTransferSuspected: externalTransferSuspected,
                    RemainingCostBasisUsd: state.CostBasisUsd,
                    EstimatedCompoundFeesUsd: agg?.EstimatedCompoundFeesUsd is null ? null : Convert.ToDecimal(agg.EstimatedCompoundFeesUsd.Value)
                )));
        }

        swTotal.Stop();
        logger.LogInformation("PnL done wallet={Wallet} tokens={TokenCount} rows={RowCount} in {ElapsedMs}ms", wallet, tokens.Count, rows.Count, swTotal.ElapsedMilliseconds);
        return new PnlResponseDto(meta, rows);
    }
}
