using Microsoft.AspNetCore.Mvc;

namespace TodoApi.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/healthz")]
    public IActionResult Healthz() => Ok(new { status = "ok" });
}

