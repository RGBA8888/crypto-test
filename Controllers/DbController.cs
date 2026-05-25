using Microsoft.AspNetCore.Mvc;
using Crypto.Services.ClickHouse;

namespace Crypto.Controllers;

[ApiController]
[Route("api/db")]
public class DbController(IClickHouseHealthService clickHouseHealthService) : ControllerBase
{
    [HttpGet("clickhouse")]
    public async Task<ActionResult<ClickHouseHealthResult>> ClickHouse(CancellationToken cancellationToken)
    {
        var result = await clickHouseHealthService.CheckAsync(cancellationToken);
        return result.Ok ? Ok(result) : StatusCode(StatusCodes.Status503ServiceUnavailable, result);
    }
}
