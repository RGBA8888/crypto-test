namespace Crypto.Contracts.Pnl;

public sealed record PnlDiagnosticsDto(
    decimal BuyAmount,
    decimal SellAmount,
    decimal TradeNetDelta,
    bool ExternalTransferSuspected,
    decimal RemainingCostBasisUsd,
    decimal? EstimatedCompoundFeesUsd);
