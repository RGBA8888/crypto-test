namespace TodoApi.Contracts.Pnl;

public sealed record PnlMetaDto(
    string Wallet,
    DateTimeOffset From,
    DateTimeOffset To,
    string CostBasisMethod,
    string CostBasisScope,
    bool IncludeTransfers);

