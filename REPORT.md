# Report — Crypto Wallet PnL Query Service

## Summary

This project implements a small, standalone HTTP API (C# / ASP.NET) that queries a ClickHouse dataset produced by a Solana indexer and returns per-token wallet PnL for a requested UTC time range.

Primary goal: provide a clean API surface and a query layer that can evolve without turning into one large controller method, while remaining safe (parameterized SQL) and reasonably efficient for time-bounded requests.

This task difficulty mainly comes from PnL semantics, not due to HTTP or ClickHouse connectivity. The API must avoid confusing:

```text
trade intent            ≠ token transfers
trade PnL               ≠ wallet balance delta
realized PnL            ≠ mark-to-market unrealized PnL
DeFi inflow/outflow     ≠ all movement into/out of wallet
```

The project deliberately returns diagnostic fields (buy/sell totals, holdings, deltas, and trace id) so consumers can reason about the quality of a computed PnL number instead of treating it as absolute truth.

## API

### Endpoints

- `GET /healthz/`
  - Unauthenticated liveness check.
- `GET /api/db/clickhouse`
  - ClickHouse connectivity health check (requires API key when `API_KEY` is configured).
- `GET /wallets/{address}/pnl?from=<utc>&to=<utc>`
  - Returns one row per token traded by the wallet in the requested range.

### Request parameters

`from` and `to` are required UTC timestamps (the service accepts):

- ISO-8601 / RFC3339 strings (recommended), e.g. `2026-05-25T02:30:00Z`
- Unix timestamps in seconds or milliseconds (numeric strings)

`costBasisScope`:

- `RangeOnly` (0): cost basis uses only trades in `[from, to)`. Fastest; may be inaccurate if the position existed before `from`.
- `Warmup` (1): attempts to include a limited warmup history (bounded window + row limit) before `from` to approximate full cost basis.

`includeTransfers`:

- Currently diagnostic-only; echoed in response meta for forward compatibility.

### Response shape

The response is JSON:

- `meta`: request echo + calculation notes
- `rows[]`: per-token metrics

Minimum required fields per token (included):

- `token`
- `realizedPnlUsd`
- `unrealizedPnlUsd`
- `netBalanceDelta`
- `closeHoldings`
- `totalBuyUsd` / `totalSellUsd`
- `latestPriceUsd`
- `tradeCount`

## Data model and query strategy (ClickHouse)

The service relies on three ClickHouse tables (in `solanav1`). The schema already provides a very useful abstraction: **`ui_trades`**. Without it, the service would need to reconstruct swap intent from lower-level raw swap records and transfers.

- `ui_trades`
  - Preferred for realized PnL/cost basis due to explicit `side` and chronological trade rows.
  - Important columns used here: `block_time`, `user_wallet`, `token`, `side`, `amount`, `volume_usd`, `tx_hash`, `compound_fees`.
- `wallet_1m_deltas`
  - Used for net balance delta and end-of-range holdings (physical changes, includes transfers).
  - Uses `argMaxMerge(close_holdings)` for close holdings.
- `token_prices_1m`
  - Used for mark price at or before `to` for unrealized PnL.

### How to think about the tables

- `ui_trades` is a **user-facing, per-token trade view**: one on-chain swap produces *two* token-centric rows (a `Buy` for the received token and a `Sell` for the spent token). This is perfect for per-token PnL, but means you should not interpret “sum of all volume_usd across tokens” as “total wallet swap volume” without considering double counting.
- `wallet_1m_deltas` is a **minute-level physical holdings table**. It captures all movement in/out of holdings (including non-trade transfers) via `delta_balance`. It is useful as a validation/diagnostic signal and as a source for end holdings.
- `token_prices_1m` provides the **mark price**. For unrealized PnL you must use the latest available price at or before `to`, not an average over the range.

### High-level flow

1. Find tokens traded by the wallet in `[from, to)` (`ui_trades`).
2. For those tokens:
   - Aggregate buys/sells and counts in `[from, to)` (`ui_trades`).
   - Fetch net balance delta and close holdings in `[from, to)` (`wallet_1m_deltas`).
   - Fetch latest price at or before `to` (`token_prices_1m`).
3. Fetch trade rows for cost basis:
   - `RangeOnly`: trade rows only in `[from, to)` (fast path).
   - `Warmup`: trade rows in a bounded warmup window ending at `to`, with a hard cap/limit.
4. Compute per-token Weighted Average Cost (WAC):
   - Buys add to inventory/cost basis.
   - Sells realize PnL vs current WAC.
5. Compute unrealized PnL:
   - `closeHoldings * latestPriceUsd - remainingCostBasisUsd`

## PnL semantics (what the numbers mean)

### Realized PnL

**Realized PnL** is profit/loss that becomes final when a position is reduced (a `Sell` happens). It requires a **cost basis method**. A naive formula like:

```text
total_sell_usd - total_buy_usd
```

is *not* realized PnL; it is net cash flow over the range.

This service computes realized PnL by processing `ui_trades` rows **chronologically** and applying a cost-basis method.

### Unrealized PnL

**Unrealized PnL** is mark-to-market PnL of the remaining position at the end of the range:

```text
unrealized_pnl_usd = close_holdings * latest_price_usd - remaining_cost_basis_usd
```

This requires:

- `close_holdings` (from `wallet_1m_deltas`, i.e. physical holdings)
- `latest_price_usd` (from `token_prices_1m` at or before `to`)
- `remaining_cost_basis_usd` (from trade-derived cost basis)

Because holdings are physical but cost basis is trade-derived, unrealized PnL can be misleading when the wallet receives/sends tokens outside of DEX trades (airdrops, transfers, bridges, etc.).

## Cost basis method: WAC vs FIFO/LIFO

This service uses **Weighted Average Cost (WAC)** because it is a good compromise for a small query API:

- Simple to implement correctly with an ordered trade stream (`ui_trades`).
- Stable and easy to explain.
- Cheap to compute in-memory after retrieving the relevant trade rows.

When FIFO/LIFO is worth it:

- If you need more precise tax/lot accounting semantics.
- If you want to match an external accounting system that expects FIFO (most common) or LIFO.

What FIFO/LIFO adds in complexity:

- You must keep a queue/stack of lots, handle partial lot closes, and be careful with floating point/precision.
- You typically need higher-fidelity handling of fees and multi-hop swaps.
- Warmup becomes more expensive because lot accounting is path-dependent and cannot be summarized as easily as WAC.

For this task, WAC provides a defensible realized/unrealized split and demonstrates correct chronological processing without turning the project into a full accounting engine.

## Warmup (why it exists, what it does)

The API asks for PnL over `[from, to)`, but realized PnL inside the range depends on the wallet's **opening position** (cost basis) at `from`.

Example:

- Before `from`: wallet bought 100 TOKEN
- In range: wallet sells 50 TOKEN

If we only scan trades inside the range, we see a sell without prior buys and cannot compute the correct realized PnL.

To address this, the `Warmup` scope attempts to fetch a bounded trade history prior to `from` (up to `CLICKHOUSE_WARMUP_MAX_DAYS`, capped by `CLICKHOUSE_TRADES_UP_TO_LIMIT`) so the in-range sells can be priced against an approximate opening cost basis.

Tradeoff:

- More correct cost basis for active wallets with recent history
- Potentially heavier queries for very high-activity wallets (hence the hard caps and the `RangeOnly` fast path)

## Fees and slippage

The dataset includes `compound_fees`. However, it is not always safe to automatically apply a fee model unless you are certain whether fees are already embedded in `volume_usd` / `price_usd`.

This service:

- Uses `volume_usd` from `ui_trades` as the primary USD notional for cost basis and realized proceeds.
- Exposes `EstimatedCompoundFeesUsd = sum(volume_usd * compound_fees)` as a **diagnostic** signal in the response.
- Does not model slippage separately; slippage is assumed to already be reflected in the executed trade price/volume fields.

## Transfers and balance deltas (why we return both)

Trade-derived token movement in range can be estimated as:

```text
trade_net_delta = buy_amount - sell_amount
```

Physical token movement in range comes from:

- `wallet_1m_deltas.delta_balance` aggregated over the range (returned as `netBalanceDelta`)

If `netBalanceDelta` differs from `trade_net_delta`, the wallet likely had non-trade transfers in/out (or other non-DEX movements). The response includes a diagnostic flag `externalTransferSuspected` for this reason.

### Example SQL (as implemented)

Token list for the wallet in range:

```sql
SELECT DISTINCT token
FROM solanav1.ui_trades
WHERE user_wallet = @wallet
  AND block_time >= @from
  AND block_time <  @to
```

Per-token aggregates in range (diagnostics):

```sql
SELECT
  token AS Token,
  count() AS TradeCount,
  sumIf(volume_usd, side = 'Buy')  AS TotalBuyUsd,
  sumIf(volume_usd, side = 'Sell') AS TotalSellUsd,
  sumIf(amount, side = 'Buy')      AS BuyAmount,
  sumIf(amount, side = 'Sell')     AS SellAmount,
  sum(volume_usd * compound_fees)  AS EstimatedCompoundFeesUsd
FROM solanav1.ui_trades
WHERE user_wallet = @wallet
  AND block_time >= @from
  AND block_time <  @to
GROUP BY token
```

Wallet deltas and end holdings:

```sql
SELECT
  token AS Token,
  sum(delta_balance) AS NetBalanceDelta,
  argMaxMerge(close_holdings) AS CloseHoldings
FROM solanav1.wallet_1m_deltas
WHERE user_wallet = @wallet
  AND minute >= toDateTime(@from)
  AND minute <  toDateTime(@to)
GROUP BY token
```

Latest price at or before `to`:

```sql
SELECT
  token AS Token,
  argMax(price, minute) AS LatestPriceUsd
FROM
(
  SELECT
    mint AS token,
    minute,
    argMaxMerge(close) AS price
  FROM solanav1.token_prices_1m
  WHERE mint IN @tokens
    AND minute <= toDateTime(@to)
  GROUP BY token, minute
)
GROUP BY token
```

### Why WAC (Weighted Average Cost)

WAC is a pragmatic default for this dataset:

- Works with a sequential trade stream.
- Stable and simple to explain.
- Does not require identifying specific lots (FIFO/LIFO) which would add complexity and potentially more query volume.

## Code structure

The code is split along HTTP vs query vs domain logic lines:

```mermaid
flowchart LR
  A["Controllers (HTTP)"] --> B["Services (PnL)"]
  B --> C["Data (ClickHouse queries via Dapper)"]
  B --> D["Domain (WAC cost basis state)"]
  C --> E["ClickHouse (ui_trades / deltas / prices)"]
```

### HTTP layer

- `Controllers/PnlController.cs`
  - Request validation (range size, address length, timestamp parsing).
- `Controllers/DbController.cs`
  - ClickHouse health endpoint.
- `Controllers/HealthController.cs`
  - Liveness endpoint.

### Service layer

- `Services/Pnl/PnlService.cs`
  - Orchestrates repository calls and performs in-memory WAC calculations.
- `Services/Pnl/UtcTimestampParser.cs`
  - Parses ISO-8601 and Unix timestamps into UTC `DateTimeOffset`.

### Query layer

- `Data/Pnl/PnlQueryRepository.cs`
  - Parameterized SQL via Dapper + `ClickHouse.Driver`.
  - Defensive caps:
    - Warmup window is bounded by `CLICKHOUSE_WARMUP_MAX_DAYS`.
    - Trade paging and hard limits protect against huge responses.

### Domain logic

- `Domain/Pnl/WeightedAverageCostState.cs`
  - Minimal state machine for WAC inventory/cost basis and realized PnL.

## Safety and operational notes

### Parameterized SQL

All ClickHouse queries use Dapper parameters (no unescaped user string interpolation).

### Defensive limits

ClickHouse is treated as the bottleneck:

- API enforces a maximum time range of 30 days.
- Warmup has a bounded lookback window and a hard row limit.
- Query timeouts are configurable via env vars.

### Auth model

Cloud Run is publicly invokable; application-level auth is enforced via `X-API-Key` header when `API_KEY` is configured.

### Telemetry

The service always returns:

- `X-Trace-Id` response header

and logs only failures (5xx / exceptions) by default to reduce noise, while preserving debuggability in Cloud Run logs.

### Health check path note (Cloud Run)

In practice, Google Frontend / Cloud Run can behave inconsistently for `/healthz` vs `/healthz/`. To keep Swagger “Try it out” reliable and keep probes simple:

- The controller route is exposed as `/healthz/`.
- The middleware normalizes `/healthz` → `/healthz/`.

## Why multiple ClickHouse queries (instead of one big query)

The implementation uses multiple focused queries because it keeps the system explainable and performant:

- Token discovery (`DISTINCT token`) is a small selective query.
- Aggregates in range (`GROUP BY token`) are efficient in ClickHouse and produce small result sets.
- Deltas and end holdings are read from the pre-aggregated minute table.
- Latest prices are fetched in batch for the involved tokens only.
- Trade rows for cost basis are fetched only when needed, with conservative caps.

This avoids shipping large raw datasets to the API process when a smaller aggregated query would do, while still allowing correct chronological accounting when necessary.

## Scalability considerations

This solution is designed for time-bounded interactive queries:

- `RangeOnly` is fast and scales well for dashboards where the user selects a short range.
- `Warmup` can become expensive for highly active wallets; the bounded warmup window + limits prevent runaway scans.

If this needs to scale to many concurrent queries for very active wallets:

- Consider pre-aggregating per-wallet/token cost basis state in a materialized view (e.g., per-hour or per-day snapshots).
- Consider caching token prices and/or per-wallet token lists for hot wallets.
- Consider moving to an async job model for long warmup queries.

## Dataset exploration notes (Marimo)

This section captures observations from exploratory work in Marimo against the live ClickHouse dataset. These are not contractual guarantees of the dataset; they are meant to guide query design and scaling decisions.

### Database layout and privileges

- The provided user had `SELECT` on `solanav1.*`. Queries that implicitly target `default.*` (e.g., `DESCRIBE TABLE ui_trades` without DB qualification) can fail.

Practical consequences:

- Prefer fully qualifying tables (`solanav1.ui_trades`, `solanav1.wallet_1m_deltas`, `solanav1.token_prices_1m`) or ensure the connection’s default database is `solanav1`.
- Use `SELECT * LIMIT 1` / `system.columns` rather than relying on `DESCRIBE` against the wrong DB.

### AggregateFunction state columns

Two key tables use ClickHouse aggregate-state columns:

- `wallet_1m_deltas.close_holdings` is an `AggregateFunction` state; read it via `argMaxMerge(close_holdings)` (end-of-range).
- `token_prices_1m.close` is an `AggregateFunction(argMax, Float64, Int64)` state; to obtain a minute-level close, use `argMaxMerge(close)` grouped by `(mint, minute)`.

“Latest price at or before `to`” is safer when implemented as:

1) `(mint, minute) -> price := argMaxMerge(close)`
2) per mint: `argMax(price, minute)` to pick the latest minute <= `to`

### Coverage and volumes (snapshot)

These numbers are approximate and time-dependent, but they are useful to reason about query cost:

- `ui_trades`
  - Coverage (min/max `block_time`): ~`2026-04-28` → `2026-05-25`
  - Total rows: ~`590M` trade rows
  - Last 24h: ~`47M` trade rows
- `wallet_1m_deltas`
  - Coverage (min/max `minute`): ~`2026-05-14` → `2026-05-25` (shorter than `ui_trades`)
  - Total rows: ~`708M` rows
  - Last 24h: ~`69M` rows
- `token_prices_1m`
  - Token id column is `mint` (not `token`)
  - Filtering out sentinel timestamps (e.g., `minute >= '2024-01-01'`) produced a meaningful min coverage around ~`2026-04-17`
  - Last 24h: ~`0.8M` rows

Key implication: `wallet_1m_deltas` may not cover the entire `ui_trades` history. For older `from/to` ranges, `closeHoldings` can be missing or incomplete. The API should treat this as “best-effort” and rely on diagnostics rather than assuming holdings are always available.

### Data quality signals

In `ui_trades`, a small but non-zero number of rows had `price_usd = 0` and/or `volume_usd = 0` in a rolling 24h window. For USD-denominated PnL:

- Keep diagnostics (`totalBuyUsd`, `totalSellUsd`, `latestPriceUsd`, and optionally “zero-price row count”) to avoid presenting PnL as absolute truth.
- If desired, filter or de-weight zero-price rows for analytics, but be explicit because it changes semantics.

### Heavy-tail behavior (scaling relevance)

Activity is strongly heavy-tailed:

- A small fraction of wallets accounts for a large fraction of trade rows.
- “Super wallets” can have tens of thousands of trade rows/day.

Implications for the API:

- Worst-case latency is dominated by a small number of hot wallets.
- Keep the **time-range cap** (e.g. 30 days) and a **hard cap** for warmup trades.
- Consider caching:
  - `token_prices_1m` latest prices (short TTL)
  - token list per wallet for hot wallets (short TTL)
- Consider an async job model for deep history requests if the product needs it.

### Potential bottlenecks and optimizations

Likely bottlenecks:

- `Warmup` cost basis for extremely active wallets (trade-row volume).
- Mark-price lookup if implemented as “scan all prices”; always restrict to `mint IN @tokens`.
- Any query that is not selective by `user_wallet` and time.

Optimizations that preserve semantics:

- Always anchor all large scans by `(user_wallet, time)`; avoid unbounded queries in the API path.
- Fetch token list first (`DISTINCT token`) and use it to constrain subsequent queries (prices, warmup).
- Prefer table-specific aggregations in ClickHouse (small result sets) over moving raw rows to the app process.
- Avoid `FINAL` unless you have explicit evidence it is required; it can be extremely expensive on large tables.

## CI/CD

GitHub Actions performs:

- build/test
- Docker build and push to Docker Hub (`crypto:<sha>`, `crypto:latest`)
- deploy to Google Cloud Run

Current Cloud Run URL:

- https://crypto-92654428298.europe-west1.run.app

Docker Hub repository:

- https://hub.docker.com/repository/docker/dysoncucumber/crypto/general

## Optional features (not implemented)

The original assignment suggested several optional extensions. This implementation intentionally keeps the API surface small and focuses on correct semantics and safe querying.

Not implemented (yet):

- `groupBy=hour|day` time series output (chartable PnL). The current endpoint returns one row per token for the whole range. A time series would require either (a) repeated cost-basis resets per bucket (not correct) or (b) carrying state across buckets (more complex) and would benefit from precomputed snapshots/materialized views.
- `includeTransfers=true` as a functional switch. The parameter exists but is currently diagnostic-only. Modeling transfers correctly means defining an accounting policy for “unknown basis” inflows (airdrops, deposits, bridges) and deciding how they affect realized PnL on subsequent sells (treat basis as 0, treat as unknown, or track separate lots).
- Multi-wallet support (comma-separated list). This is straightforward to add at the controller level, but it multiplies query volume and needs stricter request limits/rate limiting for production.
- “Top movers / top coins” endpoints. Better as a separate endpoint backed by cached aggregates to avoid turning the core `/pnl` into a heavy analytics query.
- Materialized view proposal/implementation. For production scale, snapshotting per-wallet/token cost basis state (hourly/daily) is recommended

## Riddle (one-line answer)

> “I am counted once and never change, yet my shadow changes whenever you ask. Hold me in tokens and I stay the same; price me in dollars and time answers back.”

Maybe primarily impermanent loss. More likely it is a **token-denominated position** whose dollar “shadow” is the mark-to-market value. A related interpretation is unrealized PnL. It changes whenever you change the pricing timestamp, even if the token quantity has not changed.
