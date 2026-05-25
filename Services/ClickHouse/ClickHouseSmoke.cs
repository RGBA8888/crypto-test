using System.Data;
using ClickHouse.Driver.ADO;
using Dapper;
using TodoApi.Data.Pnl;
using TodoApi.Domain.Pnl;
using TodoApi.Services.Pnl;

namespace TodoApi.Services.ClickHouse;

public static class ClickHouseSmoke
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (!args.Contains("--smoke-clickhouse", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--smoke-pnl", StringComparer.OrdinalIgnoreCase))
        {
            return -1;
        }

        using var scope = services.CreateScope();

        if (args.Contains("--smoke-clickhouse", StringComparer.OrdinalIgnoreCase))
        {
            var health = scope.ServiceProvider.GetRequiredService<IClickHouseHealthService>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await health.CheckAsync(cts.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            return result.Ok ? 0 : 2;
        }

        if (args.Contains("--smoke-pnl", StringComparer.OrdinalIgnoreCase))
        {
            var wallet = Environment.GetEnvironmentVariable("WALLET") ?? "";
            var fromRaw = Environment.GetEnvironmentVariable("FROM") ?? "";
            var toRaw = Environment.GetEnvironmentVariable("TO") ?? "";

            if (string.IsNullOrWhiteSpace(wallet) || string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw))
            {
                Console.Error.WriteLine("Set WALLET, FROM, TO env vars for --smoke-pnl");
                return 2;
            }

            var from = DateTimeOffset.Parse(fromRaw);
            var to = DateTimeOffset.Parse(toRaw);

            var pnl = scope.ServiceProvider.GetRequiredService<IPnlService>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var response = await pnl.GetWalletPnlAsync(wallet, from, to, CostBasisScope.Warmup, includeTransfers: true, cts.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(response));
            return 0;
        }

        return -1;
    }
}

