using System.Data;
using ClickHouse.Driver.ADO;

namespace TodoApi.Services.ClickHouse;

public interface IClickHouseHealthService
{
    Task<ClickHouseHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record ClickHouseHealthResult(
    bool Ok,
    long LatencyMs,
    string? Error,
    string? ServerVersion,
    string? ServerTimeZone);

public sealed class ClickHouseHealthService(
    IConfiguration configuration,
    ILogger<ClickHouseHealthService> logger) : IClickHouseHealthService
{
    public async Task<ClickHouseHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var options = BindOptions(configuration);
        var connectionString = BuildConnectionString(options);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));

            await using var connection = new ClickHouseConnection(connectionString);
            await connection.OpenAsync(timeoutCts.Token);

            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "SELECT version(), timezone()";

            await using var reader = await command.ExecuteReaderAsync(timeoutCts.Token);
            if (!await reader.ReadAsync(timeoutCts.Token))
            {
                stopwatch.Stop();
                return new ClickHouseHealthResult(false, stopwatch.ElapsedMilliseconds, "No rows returned", null, null);
            }

            var version = reader.GetString(0);
            var timeZone = reader.GetString(1);

            stopwatch.Stop();
            return new ClickHouseHealthResult(true, stopwatch.ElapsedMilliseconds, null, version, timeZone);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "ClickHouse health check failed");
            return new ClickHouseHealthResult(false, stopwatch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}", null, null);
        }
    }

    private static ClickHouseOptions BindOptions(IConfiguration configuration)
    {
        var options = new ClickHouseOptions();
        configuration.GetSection("ClickHouse").Bind(options);

        // Env var overrides (simple flat style)
        options = options with
        {
            ConnectionString = configuration["CLICKHOUSE_CONNECTION_STRING"] ?? options.ConnectionString,
            Host = configuration["CLICKHOUSE_HOST"] ?? options.Host,
            Username = configuration["CLICKHOUSE_USER"] ?? options.Username,
            Password = configuration["CLICKHOUSE_PASSWORD"] ?? options.Password,
            Database = configuration["CLICKHOUSE_DATABASE"] ?? options.Database,
            Protocol = configuration["CLICKHOUSE_PROTOCOL"] ?? options.Protocol,
            Port = int.TryParse(configuration["CLICKHOUSE_PORT"], out var port) ? port : options.Port,
            TimeoutSeconds = int.TryParse(configuration["CLICKHOUSE_TIMEOUT_SECONDS"], out var timeout) ? timeout : options.TimeoutSeconds,
        };

        return options;
    }

    private static string BuildConnectionString(ClickHouseOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString!;
        }

        var builder = new ClickHouseConnectionStringBuilder
        {
            Host = options.Host,
            Port = (ushort)Math.Clamp(options.Port, 1, 65535),
            Username = options.Username,
            Password = options.Password,
            Database = options.Database,
            Protocol = options.Protocol,
        };

        return builder.ConnectionString;
    }
}
