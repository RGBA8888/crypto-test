var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .WithName("Healthz");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi(options =>
    {
        options.DocumentPath = "/openapi/v1.json";
    });
}

app.UseHttpsRedirection();

// Temporary API-key guard for public deployments (Cloud Run).
// Exemptions: /healthz and swagger/openapi endpoints in development.
var apiKey = app.Configuration["API_KEY"];
if (!string.IsNullOrWhiteSpace(apiKey))
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isHealthz = path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
        var isDevOpenApi = app.Environment.IsDevelopment() &&
                           (path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase));

        if (isHealthz || isDevOpenApi)
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
