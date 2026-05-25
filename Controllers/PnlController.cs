using Microsoft.AspNetCore.Mvc;
using TodoApi.Contracts.Pnl;
using TodoApi.Domain.Pnl;
using TodoApi.Services.Pnl;

namespace TodoApi.Controllers;

[ApiController]
[Route("wallets")]
public class PnlController(IPnlService pnlService) : ControllerBase
{
    /// <summary>
    /// Query wallet PnL for a UTC time range.
    /// </summary>
    /// <param name="address">Wallet address.</param>
    /// <param name="from">UTC timestamp. ISO-8601 (recommended) or Unix seconds/milliseconds.</param>
    /// <param name="to">UTC timestamp. ISO-8601 (recommended) or Unix seconds/milliseconds.</param>
    /// <param name="costBasisScope">Cost-basis scope. RangeOnly is fastest; Warmup attempts a wider history.</param>
    /// <param name="includeTransfers">Currently diagnostic-only; included in response meta.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("{address}/pnl")]
    public async Task<ActionResult<PnlResponseDto>> GetPnl(
        [FromRoute] string address,
        [FromQuery] string from,
        [FromQuery] string to,
        [FromQuery] CostBasisScope costBasisScope = CostBasisScope.Warmup,
        [FromQuery] bool includeTransfers = true,
        CancellationToken cancellationToken = default)
    {
        if (!UtcTimestampParser.TryParseUtc(from, out var fromUtc, out var fromError))
        {
            return BadRequest(new { error = $"Invalid `from`: {fromError}" });
        }

        if (!UtcTimestampParser.TryParseUtc(to, out var toUtc, out var toError))
        {
            return BadRequest(new { error = $"Invalid `to`: {toError}" });
        }

        if (toUtc <= fromUtc)
        {
            return BadRequest(new { error = "`to` must be greater than `from`" });
        }

        if ((toUtc - fromUtc) > TimeSpan.FromDays(30))
        {
            return BadRequest(new { error = "Time range too large; max 30 days" });
        }

        if (address.Length is < 20 or > 60)
        {
            return BadRequest(new { error = "Invalid wallet address format" });
        }

        var result = await pnlService.GetWalletPnlAsync(address, fromUtc, toUtc, costBasisScope, includeTransfers, cancellationToken);
        return Ok(result);
    }
}
