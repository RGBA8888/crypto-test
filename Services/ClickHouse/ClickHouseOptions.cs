namespace TodoApi.Services.ClickHouse;

public sealed record ClickHouseOptions
{
    public string? ConnectionString { get; init; }

    public string Host { get; init; } = "";
    public int Port { get; init; } = 8123;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Database { get; init; } = "__default__";
    public string Protocol { get; init; } = "http";

    public int TimeoutSeconds { get; init; } = 10;
}
