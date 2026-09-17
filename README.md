# NovaWallet Ledger Service

.NET 8 / SQL Server 2022 wallet ledger. Money is integer kobo (`long` / SQL `BIGINT`); NGN is the only currency. A transfer commits both balances, two ledger entries, two audit entries and its durable idempotency response in one SQL transaction.

## Run locally

Prerequisites: Docker Engine with Compose v2 and Linux containers on an x64 machine. Allocate enough memory for SQL Server (at least 2 GB for SQL Server, plus the API/build). Initial startup downloads the SQL Server and .NET images. A local .NET SDK is not needed for Docker startup.

```sh
docker compose up --build
```

`docker compose up` is sufficient on a fresh checkout; use `--build` after changing source code. The database health check gates API startup, and the API retries SQL migration failures up to 30 attempts with two seconds between retries. The API does not accept requests before migration completes.

| Service | URL |
|---|---|
| API | http://localhost:8080 |
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI JSON | http://localhost:8080/swagger/v1/swagger.json |
| Local SQL connection | localhost,14333 |

Ports bind to localhost. Compose uses a persistent `ledger-data` volume. `docker compose down` stops the services and retains data; `docker compose down -v` also deletes that local data.

## Development JWT and sample workflow

Compose runs in Development. `POST /dev/token` issues a one-hour token for `local-evaluator`, without a request body. The route is absent in Production. In Swagger, paste the token into **Authorize** (the UI supplies the Bearer prefix).

The following Bash examples use `curl` and `jq`:

```sh
BASE=http://localhost:8080
TOKEN=$(curl -fsS -X POST "$BASE/dev/token" | jq -r .accessToken)
SUFFIX=$(date +%s)
SOURCE=$(curl -fsS "$BASE/api/wallets" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"customerId\":\"source-$SUFFIX\"}" | jq -r .id)
DEST=$(curl -fsS "$BASE/api/wallets" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"customerId\":\"destination-$SUFFIX\"}" | jq -r .id)

curl -fsS "$BASE/api/wallets/$SOURCE/credits" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"amountKobo":10000000}'

PAYLOAD="{\"sourceWalletId\":\"$SOURCE\",\"destinationWalletId\":\"$DEST\",\"amountKobo\":1000000}"
curl -fsS "$BASE/api/transfers" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: demo-$SUFFIX" -d "$PAYLOAD"

# Repeat the transfer command to replay the original result without moving money again.
curl -fsS "$BASE/api/wallets/$SOURCE/balance" -H "Authorization: Bearer $TOKEN"
curl -fsS "$BASE/api/wallets/$DEST/balance" -H "Authorization: Bearer $TOKEN"
curl -fsS "$BASE/api/wallets/$SOURCE/statement?page=1&pageSize=20" -H "Authorization: Bearer $TOKEN"
```

The source ends at 9,000,000 kobo and destination at 1,000,000 kobo. One naira is 100 kobo. For an automated PowerShell walkthrough, including assertions and replay:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke.ps1
```

Each smoke run creates two new demo wallets in the local database.

## API contracts

All five functional endpoints require a valid JWT with a nonempty `sub` claim of at most 200 characters.

| Method / route | Request / behavior |
|---|---|
| `POST /api/wallets` | `{"customerId":"CUST-001"}`; returns 201, zero balance and NGN |
| `GET /api/wallets/{id}/balance` | Wallet ID, customer ID, integer balance and currency |
| `POST /api/wallets/{id}/credits` | `{"amountKobo":10000000}`; positive integer; returns ledger entry |
| `POST /api/transfers` | Source/destination GUIDs and positive `amountKobo`; requires `Idempotency-Key`; returns 200 |
| `GET /api/wallets/{id}/statement?page=1&pageSize=20` | `{page,pageSize,totalCount,items}`; page size 1–100 |

Statements order by `(CreatedAtUtc DESC, Id DESC)`, so ties are stable. Offset pagination is not a snapshot across separate HTTP requests: new transactions can shift later pages. Types are serialized as numbers: Credit = 1, TransferDebit = 2, TransferCredit = 3.

FluentValidation handles input shape. Stateful financial decisions execute in services inside the SQL transaction. Automatic model-binding errors, authentication failures and application exceptions use Problem Details with a `code` extension.

| Status | Codes |
|---|---|
| 400 | `validation` |
| 401 / 403 | `unauthorized` / `forbidden` |
| 404 | `wallet_not_found` (or `not_found` for an unmapped route) |
| 409 | `duplicate_customer`, `idempotency_conflict` |
| 422 | `insufficient_funds`, `daily_limit_exceeded`, `balance_overflow` |
| 500 | `internal_error` |

Unexpected failures are logged server-side; SQL messages and stack traces are not returned to clients.

## Configuration

Compose accepts overrides from environment variables or an untracked `.env` file:

| Compose variable | Default |
|---|---|
| `API_PORT` | `8080` |
| `SQL_PORT` | `14333` |
| `MSSQL_SA_PASSWORD` | Development-only password in Compose |
| `JWT_ISSUER` | `NovaWallet.Local` |
| `JWT_AUDIENCE` | `NovaWallet.Api` |
| `JWT_SIGNING_KEY` | Development-only signing key in Compose |
| `DAILY_OUTBOUND_LIMIT_KOBO` | `50000000` |

The API configuration keys are `ConnectionStrings:NovaWallet`, `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SigningKey`, `WalletOptions:DailyOutboundLimitKobo`, and `Database:ApplyMigrations`. Use double underscores for environment variables, for example `Jwt__SigningKey`.

For local debugging with the .NET 8 SDK (or later):

```sh
docker compose up -d sqlserver
dotnet run --project NovaWallet.API.csproj --launch-profile http
```

This serves http://localhost:5298/swagger using the development settings. If Compose credentials or SQL port are overridden, supply the corresponding `ConnectionStrings__NovaWallet` override for the local process. Production has no default connection or JWT key and does not automatically migrate unless explicitly configured.

## Architecture and correctness

`NovaWallet.API.sln` contains the root API project and the Domain, Repositories, Service, UnitTests and IntegrationTests projects. Domain has no infrastructure dependency; Service uses Domain repository contracts; Repositories implements them with EF Core 8 and SQL Server; API wires both implementations through DI. The original solution format was converted from `.slnx` to `.sln` for .NET 8 SDK compatibility.

Transfers:

1. Begin a SQL transaction and acquire transaction-owned `sp_getapplock` for `transfer-idempotency:{key}`.
2. Check the durable record against SHA-256 of the canonical lowercase GUIDs and invariant integer amount, separated by `|`.
3. Replay the exact stored HTTP status and JSON for a match; reject a different payload with 409.
4. Acquire `UPDLOCK, HOLDLOCK` wallet locks in deterministic GUID order using separate parameterized reads.
5. Check funds and daily usage after the source lock is held. Capture time after lock acquisition using injected `TimeProvider`.
6. Mutate both balances with checked integer arithmetic; add paired ledger/audit entries and the serialized response; save and commit once.

No process-local lock participates in financial correctness. SQL locks protect competing API instances. Credit uses the same wallet-lock/transaction strategy and always writes a ledger entry and audit entry.

Daily limit: 50,000,000 kobo (NGN 500,000) by default, outbound transfer debits only. WAT is UTC+01:00; queries use UTC timestamps in `[WAT midnight, next WAT midnight)`. The limit query remains inside the source-wallet lock. Boundary arithmetic avoids `long` overflow.

Idempotency keys are required, trimmed, case-sensitive, service-wide and limited to 128 characters. SQL binary collation matches application-lock equality. Completed transfers are retained indefinitely; failed transactions do not reserve their keys. Replays return the original balance snapshots. Credits are intentionally not idempotent: retrying a credit can credit twice.

The database enforces nonnegative balances, positive ledger amounts, unique customer IDs and unique idempotency keys. Audit rows are separate, linked one-to-one to ledger rows, and protected by an `INSTEAD OF UPDATE, DELETE` trigger. EF disables SQL OUTPUT for the triggered audit table. These protections cover ordinary DML; a database administrator can still change/drop schema or disable triggers.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and [docs/VERIFICATION.md](docs/VERIFICATION.md) for acceptance evidence.

## Tests

```sh
dotnet build NovaWallet.API.sln
dotnet test NovaWallet.API.sln
# Fast deterministic tests only:
dotnet test NovaWallet.UnitTests/NovaWallet.UnitTests.csproj
```

Integration tests require a running Docker engine. Testcontainers downloads SQL Server 2022, applies the real EF migration, and gives each test a distinct database. They do not use EF InMemory and do not silently skip when Docker is unavailable. Temporary containers are cleaned up by Testcontainers.

Coverage includes 20-request overspend contention across two API hosts (exactly 10 successes and 10 insufficient-funds failures), concurrent same-key replay across hosts, fresh-host replay, daily-limit contention, WAT midnight, audit immutability, injected audit-write failure, overflow rollback, opposing transfers with concurrent credits, JWT protection, duplicate customers, database constraints and stable statement pagination. Unit tests cover canonical hashing, WAT boundaries, overflow-safe limit arithmetic and request validation.

## Assumptions and trade-offs

- This models a trusted authenticated operator API: any authenticated subject can access all wallets and credit funds. Customer ownership authorization and production identity/KYC are outside scope.
- One wallet per trimmed customer ID; customer uniqueness follows SQL Server's database collation (case-insensitive in the supplied container).
- Credit simulates an inbound payment; no payment-rail, NIBSS, BVN/NIN or external HTTP integration is performed.
- Pessimistic locking favors correctness over same-wallet write throughput. SQL Server-specific locks intentionally reduce portability.
- Stored balances provide fast reads and require ledger/audit writes in the same transaction. Arbitrary privileged database edits are outside the application's guarantee.
- Lock timeouts, deadlocks or unexpected DB failures roll back and return 500. There is no hidden automatic credit retry. Transfers can be retried with the same key, including after an ambiguous network response.
- The configured daily limit should be consistent across replicas. No idempotency expiry/retention policy, outbox, message broker or rate limiter is implemented.

Optional stretch goals are not implemented: health/readiness HTTP endpoints, custom correlation-ID propagation, transfer rate limiting and a transactional outbox. Database startup readiness is handled by the Compose health gate and migration retry loop.

## Security and deployment

The checked-in Docker/development credentials are public local defaults, never production secrets. Replace them and use a proper JWT issuer, TLS and least-privilege SQL credentials for deployment. The JWT signing key must contain at least 32 UTF-8 bytes. The development issuer is absent outside Development. Swagger is enabled in all environments and should be restricted at the deployment boundary if needed.

Compose uses `sa` for evaluator setup and migrations. In production, run migrations once as a deployment step with a separate privileged account; application instances should not race schema migrations and should not have DDL permissions. Do not expose the development token route or trusted credit API publicly. JWTs, signing keys and full customer payloads are not application log fields.

[AI_USAGE.md](AI_USAGE.md) records observed implementation corrections and verification limits. Work is committed locally in reviewable phases; nothing is pushed automatically.
