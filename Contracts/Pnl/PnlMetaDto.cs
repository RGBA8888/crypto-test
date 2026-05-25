namespace Crypto.Contracts.Pnl;

public sealed record PnlMetaDto(
    string Wallet,
    DateTimeOffset From,
    DateTimeOffset To,
    string CostBasisMethod,
    string CostBasisScope,
    bool IncludeTransfers,
    int TradesUpToLimit,
    bool TradesUpToTruncated,
    string FeesModel);
