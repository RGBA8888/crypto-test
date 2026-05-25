using Microsoft.AspNetCore.HttpOverrides;
using NSwag.Generation.Processors.Security;
using TodoApi.Services.ClickHouse;
using TodoApi.Data.Pnl;
using TodoApi.Services.Pnl;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSingleton<IClickHouseHealthService, ClickHouseHealthService>();
builder.Services.AddSingleton<IPnlQueryRepository, PnlQueryRepository>();
builder.Services.AddSingleton<IPnlService, PnlService>();
builder.Services.AddOpenApiDocument(config =>
{
    config.Title = "TodoApi";
    config.Version = "v1";
    config.Description =
        "Wallet PnL Query Service (ClickHouse-backed). " +
        "All endpoints (except /healthz) require X-API-Key when API_KEY is configured.";

    config.AddSecurity("ApiKey", Array.Empty<string>(), new NSwag.OpenApiSecurityScheme
    {
        Type = NSwag.OpenApiSecuritySchemeType.ApiKey,
        Name = "X-API-Key",
        In = NSwag.OpenApiSecurityApiKeyLocation.Header,
        Description = "Static API key for this service"
    });
    config.OperationProcessors.Add(new AspNetCoreOperationSecurityScopeProcessor("ApiKey"));
});

// Cloud Run provides the port via the PORT env var (default is 8080).
var portValue = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(portValue, out var port) && port is > 0 and < 65536)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

// Configure the HTTP request pipeline.
var swaggerEnabled =
    app.Environment.IsDevelopment() ||
    string.Equals(app.Configuration["SWAGGER_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);

if (swaggerEnabled)
{
    app.UseOpenApi(settings => { settings.Path = "/openapi/v1.json"; });
    app.UseSwaggerUi(settings =>
    {
        settings.Path = "/swagger";
        settings.DocumentPath = "/openapi/v1.json";
    });
}

// Request/response telemetry: always set trace header; log only on errors.
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HttpTelemetry");
    var startedAt = DateTimeOffset.UtcNow;
    var sw = System.Diagnostics.Stopwatch.StartNew();

    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        context.Response.Headers["X-Started-At-Utc"] = startedAt.ToString("O");
        return Task.CompletedTask;
    });

    try
    {
        await next();
        sw.Stop();

        if (context.Response.StatusCode >= 500)
        {
            logger.LogWarning(
                "HTTP {Method} {Path}{Query} -> {StatusCode} in {ElapsedMs}ms (trace={TraceId})",
                context.Request.Method,
                context.Request.Path.Value,
                context.Request.QueryString.Value,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                context.TraceIdentifier);
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(
            ex,
            "HTTP {Method} {Path}{Query} -> exception in {ElapsedMs}ms (trace={TraceId})",
            context.Request.Method,
            context.Request.Path.Value,
            context.Request.QueryString.Value,
            sw.ElapsedMilliseconds,
            context.TraceIdentifier);
        throw;
    }
});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Cloud Run forwards from dynamic IPs; trust forwarded headers in this controlled environment.
    KnownIPNetworks = { },
    KnownProxies = { }
});
// Cloud Run terminates TLS at the frontend; HTTPS redirection isn't required here.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Temporary API-key guard for public deployments (Cloud Run).
// Exemptions: /healthz and swagger/openapi endpoints in development.
var apiKey = app.Configuration["API_KEY"];
if (!string.IsNullOrWhiteSpace(apiKey))
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isHealthz = path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase);
        var isSwagger = swaggerEnabled &&
                        (path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase));

        if (isHealthz || isSwagger)
        {
            await next();
            return;
        }

        if (context.Request.Headers.TryGetValue("X-API-Key", out var headerValue) &&
            string.Equals(headerValue.ToString(), apiKey, StringComparison.Ordinal))
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
    });
}

app.UseAuthorization();

app.MapControllers();

app.Run();
