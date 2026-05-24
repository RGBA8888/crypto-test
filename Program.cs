using Microsoft.AspNetCore.HttpOverrides;
using NSwag.Generation.Processors.Security;
using TodoApi.Services.ClickHouse;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSingleton<IClickHouseHealthService, ClickHouseHealthService>();
builder.Services.AddOpenApiDocument(config =>
{
    config.Title = "TodoApi";
    config.Version = "v1";

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

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Cloud Run forwards from dynamic IPs; trust forwarded headers in this controlled environment.
    KnownNetworks = { },
    KnownProxies = { }
});

app.UseHttpsRedirection();

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
