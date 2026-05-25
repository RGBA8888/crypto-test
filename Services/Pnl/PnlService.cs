using Crypto.Contracts.Pnl;
using Crypto.Data.Pnl;
using Crypto.Domain.Pnl;

namespace Crypto.Services.Pnl;

public sealed class PnlService(IPnlQueryRepository repository, ILogger<PnlService> logger) : IPnlService
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

        // Fetch tokens traded in range
        var tokens = await repository.GetTokensTradedInRangeAsync(wallet, from, to, ct);

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
        var aggRows = await repository.GetTradesAggInRangeAsync(wallet, from, to, ct);
        var aggByToken = aggRows.ToDictionary(x => x.Token, StringComparer.Ordinal);

        // Deltas and holdings
        var deltas = await repository.GetWalletDeltasAsync(wallet, from, to, ct);
        var deltasByToken = deltas.ToDictionary(x => x.Token, StringComparer.Ordinal);

        // Prices
        var prices = await repository.GetLatestPricesAsync(tokens, to, ct);
        var priceByToken = prices.ToDictionary(x => x.Token, StringComparer.Ordinal);

        TradesUpToResult tradesResult;
        var scopeUsed = scope;
        if (scope == CostBasisScope.Warmup)
        {
            // Trades up to 'to' to compute warmup + in-range realized/remaining basis.
            // Repository already applies conservative limits and warmup window bounds.
            tradesResult = await repository.GetTradesUpToAsync(wallet, tokens, from, to, ct);
        }
        else
        {
            // Range-only mode: do not scan wallet history.
            tradesResult = await repository.GetTradesInRangeAsync(wallet, tokens, from, to, ct);
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
        logger.LogInformation(
            "PnL wallet={Wallet} scope={ScopeUsed} tokens={TokenCount} rows={RowCount} in {ElapsedMs}ms",
            wallet,
            scopeUsed,
            tokens.Count,
            rows.Count,
            swTotal.ElapsedMilliseconds);
        return new PnlResponseDto(meta, rows);
    }
}
