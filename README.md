# NovaWallet Ledger Service

A simplified wallet ledger API built with .NET 8 and SQL Server for the FirstBank Digital Factory backend take-home exercise.

The implementation is designed around one primary requirement: **money must remain correct under retries and concurrent requests**.

> This README describes the intended final implementation. After the coding agent completes the project, verify every command, route, port, package, and architectural statement against the actual code before submission.

---

## Features

- Create wallet with zero starting balance
- Retrieve wallet balance in NGN/kobo
- Credit wallet
- Atomic wallet-to-wallet transfer
- Durable transfer idempotency
- Paginated wallet statement
- Daily outbound transfer limit reset at midnight WAT
- Append-only audit trail
- JWT bearer authentication
- FluentValidation request validation
- RFC 7807 Problem Details
- Swagger/OpenAPI
- Docker Compose startup
- Automated unit and SQL Server integration tests
- Concurrency/load-focused transfer tests

---

## Money representation

All monetary values are integer kobo.

```text
₦1.00       = 100 kobo
₦10,000     = 1,000,000 kobo
₦500,000    = 50,000,000 kobo
```

C# uses:

```csharp
long
```

SQL Server uses:

```sql
BIGINT
```

No floating-point type is used in the money path.

---

## Solution structure

```text
NovaWallet.sln

NovaWallet.Api/
NovaWallet.Domain/
NovaWallet.Repositories/
NovaWallet.Service/
NovaWallet.UnitTests/
NovaWallet.IntegrationTests/

README.md
AI_USAGE.md
docs/
  ARCHITECTURE.md
  PROMPT.md

docker-compose.yml
Dockerfile
```

### Layer responsibilities

**Domain**

Business entities, enums, repository contracts, and domain concepts.

**Repository**

EF Core, SQL Server, migrations, indexes, constraints, and concurrency-specific persistence.

**Services**

Wallet and transfer orchestration, idempotency, daily-limit logic, and statement behavior.

**API**

HTTP contracts, JWT, Swagger, Problem Details, dependency injection, and request pipeline.

---

## Architecture summary

The service stores a current wallet balance for fast reads and writes one transaction record for every wallet balance mutation.

A wallet-to-wallet transfer produces:

```text
1 source balance debit
1 destination balance credit
1 source WalletTransaction
1 destination WalletTransaction
1 source AuditLog
1 destination AuditLog
1 durable IdempotencyRecord
```

All of these changes are committed in one SQL transaction.

See [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) for the detailed design.

The coding-agent implementation plan is in [docs/PROMPT.md](./docs/PROMPT.md).

---


## Validation approach

FluentValidation is used for request/input validation.

During design exploration, simpler approaches were considered, including inline controller checks, DataAnnotations, and lightweight format or regex checks. I chose FluentValidation as the consistent API-boundary validation strategy because it provides a cleaner separation of concerns, improves maintainability, keeps controllers focused on HTTP orchestration, and scales better as request models become more complex or contain nested object graphs.

The validation boundary is deliberate:

```text
FluentValidation
    -> request shape and basic input rules

NovaWallet.Service
    -> stateful and financial business rules

SQL Server
    -> final data-integrity constraints
```

Handled by FluentValidation:

```text
required customer ID
customer ID length
positive AmountKobo
required source/destination wallet IDs
source wallet != destination wallet
pagination bounds
idempotency-key format/length at the API boundary
```

Intentionally **not** handled by FluentValidation:

```text
wallet existence
insufficient funds
daily outbound limit
duplicate customer
idempotency conflicts
current wallet balance
```

Those rules depend on persisted state and belong in `NovaWallet.Service`. Where correctness depends on concurrency, they execute inside the appropriate database transaction and locking boundary.

SQL Server constraints remain the final integrity layer.

Validation failures are normalized into the same RFC 7807 Problem Details format as other API errors so clients receive one consistent error contract.

## Concurrency model

Correctness is enforced at the SQL Server boundary rather than with application-memory locks.

The transfer flow:

1. acquires a transaction-scoped lock for the idempotency key;
2. locks both participating wallet rows using `UPDLOCK, HOLDLOCK`;
3. acquires wallet locks in deterministic ID order;
4. checks source funds;
5. checks the current WAT daily outbound total;
6. applies source debit and destination credit;
7. writes transaction and audit records;
8. persists the idempotent response;
9. commits once.

This prevents concurrent requests from independently observing the same spendable source balance.

The database also has a `CHECK (BalanceKobo >= 0)` constraint as a final safety invariant.

---

## Idempotency

`POST /api/transfers` requires:

```http
Idempotency-Key: <unique-key>
```

Behavior:

| Request | Result |
|---|---|
| new key + valid payload | transfer is processed |
| same key + same payload | stored result is replayed; money does not move again |
| same key + different payload | `409 Conflict` |
| missing key | `400 Bad Request` |

The payload identity is a SHA-256 hash of a canonical representation of:

```text
SourceWalletId
DestinationWalletId
AmountKobo
```

Idempotency state is stored in SQL Server and therefore does not depend on one API process remaining alive.

---

## Daily outbound limit

Default:

```text
₦500,000/day
50,000,000 kobo/day
```

The value is server-side configuration.

The limit resets at midnight WAT (`UTC+01:00`).

All timestamps are stored in UTC. The service converts WAT midnight boundaries to UTC for the daily outbound query.

The daily-limit query executes while the source wallet is locked inside the same transfer transaction, preventing concurrent requests from both passing a stale limit check.

---

## API

Expected routes:

```http
POST /api/wallets
GET  /api/wallets/{walletId}/balance
POST /api/wallets/{walletId}/credits
POST /api/transfers
GET  /api/wallets/{walletId}/statement?page=1&pageSize=20
```

All functional endpoints require a JWT bearer token.

### Create wallet

```http
POST /api/wallets
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "customerId": "CUST-001"
}
```

### Get balance

```http
GET /api/wallets/{walletId}/balance
Authorization: Bearer <token>
```

### Credit wallet

```http
POST /api/wallets/{walletId}/credits
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "amountKobo": 10000000
}
```

### Transfer

```http
POST /api/transfers
Authorization: Bearer <token>
Idempotency-Key: demo-transfer-001
Content-Type: application/json
```

```json
{
  "sourceWalletId": "00000000-0000-0000-0000-000000000001",
  "destinationWalletId": "00000000-0000-0000-0000-000000000002",
  "amountKobo": 1000000
}
```

### Statement

```http
GET /api/wallets/{walletId}/statement?page=1&pageSize=20
Authorization: Bearer <token>
```

Transactions are returned newest first.

---

## Running with Docker

### Prerequisite

Docker Desktop or another Docker Engine with Compose support.

### Start

```bash
docker compose up
```

Expected URL:

```text
API:     http://localhost:8080
Swagger: http://localhost:8080/swagger
```

> Verify the final ports after implementation and update this README if they differ.

On startup the API should wait/retry until SQL Server is ready and then apply EF Core migrations.

### Stop

```bash
docker compose down
```

To also remove local database volumes:

```bash
docker compose down -v
```

---

## JWT for local evaluation

The service uses configurable JWT bearer validation.

Expected configuration keys:

```text
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
```

If a development-only token endpoint is implemented, document its exact route and sample request here after implementation.

Do not expose a development token issuer in Production.

### TODO after implementation

Replace this section with one verified method for obtaining a local test JWT and a copy/paste-ready example.

---

## Configuration

Expected configuration:

```json
{
  "WalletOptions": {
    "DailyOutboundLimitKobo": 50000000
  },
  "Jwt": {
    "Issuer": "NovaWallet.Local",
    "Audience": "NovaWallet.Api",
    "SigningKey": "supplied-through-environment"
  }
}
```

Local Docker defaults may exist so that `docker compose up` works without setup. They are development credentials only and must be environment-overridable.

---

## Error responses

Errors use RFC 7807 Problem Details.

Examples of machine-readable error codes may include:

```text
wallet_not_found
insufficient_funds
daily_limit_exceeded
idempotency_conflict
```

Expected mappings:

```text
400 - invalid request / missing idempotency key
404 - wallet not found
409 - duplicate customer / idempotency conflict
422 - insufficient funds / daily limit exceeded
500 - unexpected internal error
```

Internal SQL or stack-trace details are not returned to clients.

---

## Tests

Run:

```bash
dotnet test
```

The integration test suite uses a real SQL Server engine.

Key concurrency scenarios:

### Overspend protection

A funded source wallet receives 20 concurrent transfer requests where only 10 can be covered.

Expected:

```text
10 succeed
10 are rejected
source ends at 0
destination receives exactly the funded amount
no balance becomes negative
```

### Concurrent idempotent replay

Multiple identical concurrent requests use the same idempotency key.

Expected:

```text
one financial transfer
one debit row
one credit row
one idempotency record
replays return the original result
```

### Daily limit contention

Concurrent transfers are arranged so their combined value would exceed the configured daily limit.

Expected:

```text
committed outbound value never exceeds the limit
```

### Audit immutability

A direct SQL attempt to update/delete an audit record is rejected by the database.

---

## Security notes

- All functional endpoints require JWT authentication.
- Secrets are configuration/environment values.
- Tokens and signing keys are not logged.
- Inputs are validated.
- SQL access is parameterized through EF Core/raw parameterized commands.
- Error responses do not expose internal database details.
- Audit actor information may record the authenticated `sub` claim.

This is a take-home implementation, not a complete production identity or KYC platform.

---

## Audit trail

Every balance mutation creates an `AuditLog` record separate from normal transaction history.

The application does not expose audit mutation endpoints.

The database additionally rejects `UPDATE` and `DELETE` on the audit table.

---

## Assumptions

- Currency is NGN only.
- One wallet exists per supplied customer ID.
- Credit simulates a successful inbound NIP transfer; no actual NIBSS integration is performed.
- The transfer daily limit applies only to outbound wallet-to-wallet transfer debits.
- WAT is treated as UTC+1.
- External KYC, NIN/BVN, USSD, and payment-rail integration are out of scope.
- Transfer idempotency keys are treated as unique within this service.
- The selected datastore is SQL Server.

Any implementation change to these assumptions should be documented here before submission.

---

## Design trade-offs

### Pessimistic database locking

The service deliberately serializes competing mutations against the same wallet for correctness.

This reduces same-wallet write concurrency but avoids double-spend behavior and remains valid with multiple API instances.

### Stored current balance

The wallet stores its current balance instead of recomputing it from all transaction history on every request.

To prevent inconsistency, balance changes, transaction history, audit history, and transfer idempotency are committed atomically.

### SQL Server-specific concurrency behavior

The implementation uses SQL Server primitives such as `UPDLOCK`, `HOLDLOCK`, and a transaction-owned application lock.

This reduces datastore portability but makes the concurrency contract explicit for the selected database.

---

## AI-assisted development

AI tooling is intentionally used during architecture review and implementation.

The workflow is:

```text
requirements
-> architecture/invariants
-> detailed implementation prompt
-> agent implementation
-> code review
-> adversarial/concurrency tests
-> corrections
```

See [AI_USAGE.md](./AI_USAGE.md) for the tools, prompts, outputs, and at least one concrete case where AI-generated or AI-suggested work required correction.

---

## Stretch goals

Only list items here if they are actually implemented:

- [ ] health/readiness endpoints
- [ ] request correlation IDs
- [ ] transfer rate limiting
- [ ] transactional outbox with `TransferCompleted`

Remove or mark items accurately before submission.

---

## Final submission check

Before sending the repository:

```text
[ ] docker compose up works from a clean checkout
[ ] Swagger loads
[ ] JWT flow is documented and verified
[ ] all required endpoints work
[ ] dotnet test passes
[ ] concurrency tests use real SQL Server
[ ] README commands are copy/paste verified
[ ] AI_USAGE.md contains actual, truthful examples
[ ] no real secrets are committed
[ ] no TODO text remains that should have been resolved
```


## Incremental AI-assisted implementation

The coding agent is instructed to work in logical phases and create small local Git commits after each verified phase rather than producing one large AI-generated commit. It must not push automatically.

Expected history is roughly:

```text
feat: add wallet ledger domain and persistence foundation
feat: add wallet operations authentication and API contracts
feat: add atomic transfer idempotency and daily limits
test: add ledger concurrency and idempotency integration coverage
chore: add dockerized local environment and service readiness
docs: finalize architecture usage and run instructions
```

This README currently describes the target design. Before submission it must be checked against the actual implementation and updated so every command, route, port, test claim, and architecture statement is accurate.
