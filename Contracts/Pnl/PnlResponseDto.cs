namespace TodoApi.Contracts.Pnl;

public sealed record PnlResponseDto(
    PnlMetaDto Meta,
    IReadOnlyList<PnlRowDto> Rows);

