using Crypto.Contracts.Pnl;
using Crypto.Domain.Pnl;

namespace Crypto.Services.Pnl;

public interface IPnlService
{
    Task<PnlResponseDto> GetWalletPnlAsync(string wallet, DateTimeOffset from, DateTimeOffset to, CostBasisScope scope, bool includeTransfers, CancellationToken ct);
}
