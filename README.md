# Crypto — Wallet PnL Query Service (ClickHouse)

This repository contains the source code for a small HTTP API written in C# that queries a ClickHouse dataset (Solana DEX trades) and returns per-token wallet PnL for a requested UTC time range.

## What it does

Primary endpoint:

- `GET /wallets/{address}/pnl?from=<utc>&to=<utc>`

Returns one row per token traded by the wallet in the requested range, including:

- `token`
- `realizedPnlUsd`
- `unrealizedPnlUsd`
- `netBalanceDelta`
- `closeHoldings`
- `totalBuyUsd` / `totalSellUsd` (diagnostics)
- `latestPriceUsd` used for unrealized PnL
- `tradeCount`

## Timestamps (`from`, `to`)

`from` and `to` are required and interpreted as UTC. The API accepts:

- ISO-8601 / RFC3339 (recommended), e.g. `2026-05-25T02:30:00Z`
- Unix timestamp seconds or milliseconds, e.g. `1748149800` or `1748149800000`

## Auth

If `API_KEY` is set, all endpoints except `/healthz` require:

- Header: `X-API-Key: <value>`

## Swagger / OpenAPI

When Swagger is enabled, OpenAPI is served at:

- `GET /openapi/v1.json`
- Swagger UI: `/swagger`

## Local run

Run with the .NET SDK:

```bash
export SWAGGER_ENABLED=true
export API_KEY=very-secret-key

export CLICKHOUSE_PROTOCOL=http
export CLICKHOUSE_HOST=...
export CLICKHOUSE_PORT=8123
export CLICKHOUSE_DATABASE=solanav1
export CLICKHOUSE_USER=...
export CLICKHOUSE_PASSWORD=...

dotnet run -c Release
```

Or build a container:

```bash
docker build -t crypto:local .
docker run --rm -p 8080:8080 \
  -e SWAGGER_ENABLED=true \
  -e API_KEY=very-secret-key \
  -e CLICKHOUSE_PROTOCOL=http \
  -e CLICKHOUSE_HOST=... \
  -e CLICKHOUSE_PORT=8123 \
  -e CLICKHOUSE_DATABASE=solanav1 \
  -e CLICKHOUSE_USER=... \
  -e CLICKHOUSE_PASSWORD=... \
  crypto:local
```

Health check:

```bash
curl http://localhost:8080/healthz/
```

## CI/CD and deployment

- Source code is hosted on GitHub: [RGBA8888/crypto-test](https://github.com/RGBA8888/crypto-test)
- GitHub Actions builds and tests the service, then builds a Docker image and pushes it to Docker Hub.
- After publish, GitHub Actions deploys the image to Google Cloud Run.

Current Cloud Run service URL:

- https://crypto-92654428298.europe-west1.run.app

## More details

See `REPORT.md` for architecture, query strategy, tradeoffs, and scaling notes.
