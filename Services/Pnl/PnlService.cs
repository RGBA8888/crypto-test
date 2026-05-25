using TodoApi.Contracts.Pnl;
using TodoApi.Data.Pnl;
using TodoApi.Domain.Pnl;

namespace TodoApi.Services.Pnl;

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
        // Fetch tokens traded in range
        var tokens = await repository.GetTokensTradedInRangeAsync(wallet, from, to, ct);

        var meta = new PnlMetaDto(
            Wallet: wallet,
            From: from,
            To: to,
            CostBasisMethod: "weighted_average",
            CostBasisScope: scope == CostBasisScope.Warmup ? "warmup" : "range_only",
            IncludeTransfers: includeTransfers);

        if (tokens.Count == 0)
        {
            return new PnlResponseDto(meta, Array.Empty<PnlRowDto>());
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

        // Trades up to 'to' to compute warmup + in-range realized/remaining basis
        var tradesUpTo = await repository.GetTradesUpToAsync(wallet, tokens, to, ct);

        var stateByToken = tokens.ToDictionary(t => t, _ => new WeightedAverageCostState(), StringComparer.Ordinal);

        foreach (var trade in tradesUpTo)
        {
            if (!stateByToken.TryGetValue(trade.Token, out var state))
                continue;

            // Apply warmup trades only if scope is Warmup; always apply in-range trades.
            var inRange = trade.BlockTime >= from && trade.BlockTime < to;
            if (!inRange && scope != CostBasisScope.Warmup)
                continue;

            if (string.Equals(trade.Side, "Buy", StringComparison.OrdinalIgnoreCase))
            {
                state.Buy(trade.Amount, trade.VolumeUsd);
            }
            else if (string.Equals(trade.Side, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                state.Sell(trade.Amount, trade.VolumeUsd);
            }
        }

        var rows = new List<PnlRowDto>(tokens.Count);

        foreach (var token in tokens)
        {
            aggByToken.TryGetValue(token, out var agg);
            deltasByToken.TryGetValue(token, out var delta);
            priceByToken.TryGetValue(token, out var price);
            var state = stateByToken[token];

            var buyAmount = agg?.BuyAmount ?? 0m;
            var sellAmount = agg?.SellAmount ?? 0m;
            var tradeNetDelta = buyAmount - sellAmount;

            var netBalanceDelta = delta?.NetBalanceDelta ?? 0m;
            var closeHoldings = delta?.CloseHoldings;

            var externalTransferSuspected = includeTransfers &&
                                            Math.Abs((double)(netBalanceDelta - tradeNetDelta)) > 1e-9;

            var latestPriceUsd = price?.LatestPriceUsd;
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
                TotalBuyUsd: agg?.TotalBuyUsd ?? 0m,
                TotalSellUsd: agg?.TotalSellUsd ?? 0m,
                LatestPriceUsd: latestPriceUsd,
                TradeCount: agg?.TradeCount ?? 0,
                Diagnostics: new PnlDiagnosticsDto(
                    BuyAmount: buyAmount,
                    SellAmount: sellAmount,
                    TradeNetDelta: tradeNetDelta,
                    ExternalTransferSuspected: externalTransferSuspected,
                    RemainingCostBasisUsd: state.CostBasisUsd
                )));
        }

        return new PnlResponseDto(meta, rows);
    }
}

