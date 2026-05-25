using Microsoft.AspNetCore.Mvc;
using TodoApi.Contracts.Pnl;
using TodoApi.Domain.Pnl;
using TodoApi.Services.Pnl;

namespace TodoApi.Controllers;

[ApiController]
[Route("wallets")]
public class PnlController(IPnlService pnlService) : ControllerBase
{
    [HttpGet("{address}/pnl")]
    public async Task<ActionResult<PnlResponseDto>> GetPnl(
        [FromRoute] string address,
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] CostBasisScope costBasisScope = CostBasisScope.Warmup,
        [FromQuery] bool includeTransfers = true,
        CancellationToken cancellationToken = default)
    {
        if (to <= from)
        {
            return BadRequest(new { error = "`to` must be greater than `from`" });
        }

        if ((to - from) > TimeSpan.FromDays(30))
        {
            return BadRequest(new { error = "Time range too large; max 30 days" });
        }

        if (address.Length is < 20 or > 60)
        {
            return BadRequest(new { error = "Invalid wallet address format" });
        }

        var result = await pnlService.GetWalletPnlAsync(address, from, to, costBasisScope, includeTransfers, cancellationToken);
        return Ok(result);
    }
}

