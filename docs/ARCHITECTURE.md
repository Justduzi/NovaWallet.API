# NovaWallet Ledger Service — Architecture

## 1. Purpose

NovaWallet is a simplified financial wallet ledger implemented in .NET 8. The design prioritizes correctness over feature breadth.

The system must guarantee these invariants:

1. Monetary values are represented only as integer kobo (`long` / SQL `BIGINT`).
2. A wallet balance can never become negative.
3. A transfer debits one wallet and credits another atomically.
4. Concurrent requests cannot cause a double-spend.
5. The same `Idempotency-Key` + same payload is processed once and replayed safely.
6. The same `Idempotency-Key` + different payload is rejected.
7. The daily outbound transfer limit is checked inside the same concurrency boundary as the debit.
8. Every balance mutation creates an immutable audit record.
9. The service and SQL Server start with `docker compose up`.
10. Financial correctness is verified using integration tests against a real SQL Server engine.

---

## 2. Proposed solution structure

The existing Visual Studio solution uses these exact project names:

```text
NovaWallet.API.sln

NovaWallet.API.csproj  # API is at repository root
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

Keep `README.md` and `AI_USAGE.md` at repository root because they are submission-facing deliverables. Keep the engineering design and coding-agent instructions under `docs/`.

### NovaWallet.Domain

Contains business/domain types only:

- `Wallet`
- `WalletTransaction`
- `AuditLog`
- `IdempotencyRecord`
- enums such as `WalletTransactionType`
- repository contracts/interfaces
- domain exceptions or result types where useful
- DTO-independent business constants

This project should not depend on EF Core, ASP.NET Core, SQL Server, or the API project.

### NovaWallet.Repositories

Contains persistence concerns:

- `NovaWalletDbContext`
- EF Core entity configurations
- SQL Server-specific locking queries
- repository implementations
- migrations
- database indexes and constraints
- transaction boundary helpers if needed

### NovaWallet.Service

Contains application/business logic:

- wallet creation
- balance retrieval
- wallet crediting
- transfer orchestration
- idempotency handling
- daily-limit calculation
- statement retrieval

The service layer should depend on abstractions rather than HTTP concepts.

### NovaWallet.Api

Contains:

- controllers/endpoints
- request/response DTOs
- JWT configuration
- Problem Details / exception mapping
- Swagger/OpenAPI
- health endpoints if implemented
- dependency injection
- request correlation/logging if implemented

---

## 3. Database choice

Use SQL Server 2022.

Reasons:

- The concurrency behavior must be validated using a real relational database.
- SQL Server supports row-level update locks and transaction-scoped application locks.
- It allows the service to enforce correctness in the database rather than relying on process-local locks.
- It works cleanly in Docker for the take-home environment.

EF Core is used for persistence, but concurrency-critical operations may use targeted raw SQL where SQL Server locking semantics must be explicit.

---

## 4. Core data model

### Wallet

```text
Id                  UNIQUEIDENTIFIER PK
CustomerId          NVARCHAR(100) NOT NULL UNIQUE
BalanceKobo         BIGINT NOT NULL
Currency            CHAR(3) NOT NULL DEFAULT 'NGN'
CreatedAtUtc        DATETIME2 NOT NULL
UpdatedAtUtc        DATETIME2 NOT NULL
```

Constraints:

```text
CHECK (BalanceKobo >= 0)
```

The database check is a final safety net. Application logic must still reject insufficient funds before attempting the update.

### WalletTransaction

One row represents one balance mutation for one wallet.

```text
Id                  UNIQUEIDENTIFIER PK
WalletId            UNIQUEIDENTIFIER NOT NULL
Reference           UNIQUEIDENTIFIER NOT NULL
Type                INT NOT NULL
AmountKobo          BIGINT NOT NULL
BalanceBeforeKobo   BIGINT NOT NULL
BalanceAfterKobo    BIGINT NOT NULL
CounterpartyWalletId UNIQUEIDENTIFIER NULL
CreatedAtUtc        DATETIME2 NOT NULL
```

Suggested transaction types:

```text
1 = Credit
2 = TransferDebit
3 = TransferCredit
```

A transfer creates two rows sharing the same `Reference`:

- source: `TransferDebit`
- destination: `TransferCredit`

Constraints:

```text
CHECK (AmountKobo > 0)
CHECK (BalanceBeforeKobo >= 0)
CHECK (BalanceAfterKobo >= 0)
```

Indexes:

```text
IX_WalletTransaction_WalletId_CreatedAtUtc
IX_WalletTransaction_WalletId_Type_CreatedAtUtc
IX_WalletTransaction_Reference
```

The second index supports daily outbound-limit calculations.

### AuditLog

The audit log is separate from `WalletTransaction`.

```text
Id                  UNIQUEIDENTIFIER PK
WalletId            UNIQUEIDENTIFIER NOT NULL
WalletTransactionId UNIQUEIDENTIFIER NOT NULL
MutationType        NVARCHAR(50) NOT NULL
DeltaKobo           BIGINT NOT NULL
BalanceBeforeKobo   BIGINT NOT NULL
BalanceAfterKobo    BIGINT NOT NULL
ActorSubject        NVARCHAR(200) NULL
CorrelationId       NVARCHAR(100) NULL
CreatedAtUtc        DATETIME2 NOT NULL
```

The application only inserts audit records.

To make the requirement stronger than an application convention, add a SQL Server trigger that rejects `UPDATE` and `DELETE` against `AuditLogs`.

### IdempotencyRecord

```text
Id                  UNIQUEIDENTIFIER PK
IdempotencyKey      NVARCHAR(128) NOT NULL UNIQUE
RequestHash         CHAR(64) NOT NULL
ResponseStatusCode  INT NOT NULL
ResponseBody        NVARCHAR(MAX) NOT NULL
CreatedAtUtc        DATETIME2 NOT NULL
```

`RequestHash` is SHA-256 over a canonical representation of:

```text
SourceWalletId
DestinationWalletId
AmountKobo
```

Example canonical input:

```text
{source-guid-lowercase}|{destination-guid-lowercase}|{amount-kobo}
```

Do not hash raw JSON because JSON property order and formatting should not change request identity.

---

## 5. Transfer concurrency strategy

Do not use `lock`, `SemaphoreSlim`, or any process-local synchronization for correctness.

Those approaches fail when more than one API instance is running.

### Transaction algorithm

Every transfer runs inside one SQL transaction.

Recommended sequence:

1. Validate request shape before opening the DB transaction.
2. Begin SQL transaction.
3. Acquire a transaction-scoped SQL Server application lock for the idempotency key.
4. Read `IdempotencyRecord` for the key.
5. If a record exists:
   - same request hash -> return the stored response without moving money;
   - different request hash -> return an idempotency conflict.
6. Lock both wallet rows using `UPDLOCK, HOLDLOCK`.
7. Acquire wallet locks in deterministic `WalletId` order to reduce deadlock risk.
8. Resolve which locked wallet is source and destination.
9. Reject source == destination.
10. Verify source balance is sufficient.
11. Calculate the current WAT calendar-day UTC boundaries.
12. Query the source wallet's successful outbound transfer amount for that WAT day.
13. Reject if `alreadySent + requestedAmount` exceeds the configured daily limit.
14. Debit source.
15. Credit destination.
16. Insert source and destination `WalletTransaction` rows.
17. Insert corresponding `AuditLog` rows.
18. Insert the completed `IdempotencyRecord`, including the serialized response.
19. Save changes.
20. Commit.
21. Return the transfer response.

If any step fails, the entire transaction is rolled back.

### Why the daily limit check belongs inside this transaction

The following is unsafe:

```text
query today's sent amount
check remaining limit
later begin transfer transaction
```

Two concurrent requests can both observe the same remaining limit and both pass.

Because transfers from one source wallet are serialized using the wallet row lock, the daily-limit query performed after obtaining that lock sees a safe ordering of outbound transfers.

---

## 6. SQL Server locking

For concurrency-critical wallet reads use an explicit update lock, conceptually:

```sql
SELECT *
FROM Wallets WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @WalletId;
```

Lock both wallets in ascending GUID order.

`UPDLOCK` prevents two transfer transactions from independently reading the same source balance and both deciding that funds are available.

`HOLDLOCK` keeps the lock until the transaction completes.

The implementation should keep the locked transaction short and must not call external services while locks are held.

### Idempotency lock

Use `sp_getapplock` with:

```text
Resource    = "transfer-idempotency:{key}"
LockMode    = "Exclusive"
LockOwner   = "Transaction"
```

This prevents two requests carrying the same idempotency key from racing to create the first idempotency record.

---

## 7. Wallet credit strategy

Credit is also a balance mutation.

Algorithm:

1. Begin transaction.
2. Lock wallet using `UPDLOCK, HOLDLOCK`.
3. Read current balance.
4. Add `AmountKobo`.
5. Insert `WalletTransaction` with type `Credit`.
6. Insert `AuditLog`.
7. Save.
8. Commit.

The brief does not require idempotency for the credit endpoint, so do not expand scope unless there is time and a documented reason.

---

## 8. Daily outbound limit

Default:

```text
₦500,000/day = 50,000,000 kobo/day
```

Make the limit configurable:

```text
WalletOptions:DailyOutboundLimitKobo
```

The reset is midnight WAT.

WAT is UTC+1.

Store timestamps in UTC. For a request at the current instant:

1. Obtain current UTC time through injected `.NET TimeProvider`.
2. Convert to WAT (`UTC+01:00`).
3. Calculate WAT midnight and next midnight.
4. Convert both boundaries back to UTC.
5. Sum `TransferDebit` rows for the source wallet in `[startUtc, endUtc)`.

`TimeProvider` makes boundary behavior testable without relying on the machine clock.

---

## 9. Idempotency behavior

Transfer requires the header:

```http
Idempotency-Key: <non-empty value, max 128 chars>
```

Behavior:

| Situation | Result |
|---|---|
| New key + valid payload | Process transfer |
| Same key + same payload | Return stored original response; do not mutate balances |
| Same key + different payload | `409 Conflict` |
| Missing key | `400 Bad Request` |

Do not use an in-memory cache as the source of truth.

The idempotency record must be durable in SQL Server.

---

## 10. API surface

Suggested routes:

```http
POST /api/wallets
GET  /api/wallets/{walletId}/balance
POST /api/wallets/{walletId}/credits
POST /api/transfers
GET  /api/wallets/{walletId}/statement?page=1&pageSize=20
```

### Create wallet

```json
{
  "customerId": "CUST-001"
}
```

Response includes wallet ID, customer ID, zero balance, and NGN.

### Credit wallet

```json
{
  "amountKobo": 10000000
}
```

All amounts are integer kobo.

### Transfer

Header:

```http
Idempotency-Key: 4f7852d8-...
```

Body:

```json
{
  "sourceWalletId": "guid",
  "destinationWalletId": "guid",
  "amountKobo": 1000000
}
```

### Statement

Newest first.

Response should include pagination metadata and transaction items.

Use `(CreatedAtUtc DESC, Id DESC)` as a stable ordering.

---

## 11. Validation design

Request validation uses **FluentValidation** as an explicit architectural choice.

During AI-assisted design exploration, simpler validation approaches were considered, including inline controller checks, DataAnnotations, and lightweight format or regex checks. Those approaches can work for small APIs, but they tend to scatter validation concerns across controllers and request models as the application grows.

FluentValidation was selected because it:

- keeps request validation centralized and discoverable;
- improves maintainability by avoiding repeated manual checks across controllers;
- keeps controllers focused on HTTP orchestration;
- supports conditional rules and more complex or nested object graphs cleanly;
- provides one consistent validation style across the API;
- integrates cleanly with the RFC 7807 Problem Details error contract.

The design deliberately separates request validation from financial and state-dependent business validation.

### FluentValidation responsibilities

Use FluentValidation for request/DTO rules such as:

```text
customer ID is required
customer ID length is bounded
amount must be > 0
source wallet ID is supplied
destination wallet ID is supplied
source and destination wallet IDs differ
page >= 1
page size is within the supported range
```

For the `Idempotency-Key` header, validate at the API boundary that it is required, trimmed, non-empty, and no longer than 128 characters.

Avoid mixing FluentValidation and DataAnnotations for the same DTO rules unless there is a concrete framework reason.

Never accept `double`, `float`, or decimal monetary DTOs.

### Service/domain responsibilities

Do **not** use FluentValidation for stateful or financial rules such as:

```text
wallet existence
duplicate customer wallet
insufficient funds
daily outbound transfer limit
idempotency-key reuse
current wallet balance
```

Those rules depend on persisted state and belong in `NovaWallet.Service`.

Where a rule is concurrency-sensitive, such as insufficient funds, daily transfer limits, or idempotency state, it must execute inside the relevant SQL transaction and locking boundary rather than as a pre-request validation check.

This separation reduces the risk of time-of-check/time-of-use errors where a validator approves a condition that changes before the financial mutation is committed.

### Database responsibilities

SQL Server remains the final integrity layer through constraints such as:

```text
CHECK (BalanceKobo >= 0)
CHECK (AmountKobo > 0)
UNIQUE CustomerId
UNIQUE IdempotencyKey
```

The validation flow is:

```text
HTTP request
    -> FluentValidation
    -> API/controller
    -> NovaWallet.Service business/financial rules
    -> NovaWallet.Repositories
    -> SQL Server integrity constraints
```

FluentValidation failures are mapped into the same RFC 7807 Problem Details response format used by the rest of the API.

---

## 12. Authentication

All functional wallet endpoints require JWT bearer authentication.

The goal is middleware and claims handling, not a production identity server.

Recommended development setup:

- configurable issuer
- configurable audience
- configurable symmetric signing key supplied through environment/configuration
- `[Authorize]` on wallet endpoints
- read the `sub` claim and include it in audit records when present
- Swagger configured with Bearer authentication

For panel convenience, a development-only token endpoint may be added if and only if it is disabled outside the Development environment.

Do not hard-code a real secret.

---

## 13. Error handling

Use RFC 7807 Problem Details consistently.

Suggested status mappings:

```text
400 Bad Request
    validation errors
    missing/invalid Idempotency-Key

404 Not Found
    wallet not found

409 Conflict
    customer already has a wallet
    idempotency key reused with a different payload

422 Unprocessable Entity
    insufficient funds
    daily outbound limit exceeded

500 Internal Server Error
    unexpected failures
```

Add a stable machine-readable error code in Problem Details extensions where useful, for example:

```text
insufficient_funds
daily_limit_exceeded
idempotency_conflict
wallet_not_found
```

Do not expose stack traces or SQL details.

---

## 14. Testing strategy

### Unit tests

Use unit tests for deterministic logic such as:

- request-hash generation
- WAT day-boundary calculations
- validation
- daily-limit arithmetic where isolated

### Integration tests

Concurrency behavior must be tested against real SQL Server, not EF Core InMemory.

Preferred approach:

```text
xUnit
Microsoft.AspNetCore.Mvc.Testing
Testcontainers.MsSql
```

Apply real migrations to the test database.

#### Required concurrency test

Example:

1. Create source wallet with `10,000,000` kobo.
2. Create destination wallet with `0`.
3. Launch 20 transfers concurrently.
4. Each attempts `1,000,000` kobo.
5. Use a unique idempotency key for each request.
6. Assert exactly 10 transfers succeed.
7. Assert exactly 10 fail with insufficient funds.
8. Assert source final balance is `0`.
9. Assert destination final balance is `10,000,000`.
10. Assert no wallet transaction or audit record shows a negative balance.
11. Assert ledger/audit counts match the successful transfers.

This test is intentionally stronger than simply racing two requests.

#### Concurrent idempotency test

Launch multiple identical transfer requests concurrently using the same key.

Assert:

- money moves once
- all successful replays represent the same transfer result
- only one transfer debit and one transfer credit exist
- one durable idempotency record exists

#### Concurrent daily-limit test

Configure a lower limit for the test.

Launch requests whose combined value would exceed the limit if evaluated independently.

Assert the committed total never exceeds the configured daily limit.

---

## 15. Audit immutability

The service never exposes update/delete endpoints for audit data.

Additionally, create a database trigger that rejects:

```sql
UPDATE AuditLogs
DELETE AuditLogs
```

The test suite should contain a database-level test proving that modification is rejected.

---

## 16. Docker and startup

`docker compose up` must start:

```text
api
sqlserver
```

The API should retry the database connection during startup and apply EF Core migrations automatically.

Expected service URLs may be:

```text
API:     http://localhost:8080
Swagger: http://localhost:8080/swagger
```

Use local-development defaults in `docker-compose.yml` so the evaluator can run one command, but allow every secret/configuration value to be overridden by environment variables.

Do not represent local development credentials as production-safe secrets.

---

## 17. Logging

Use structured `ILogger` logging.

Never log:

- JWT tokens
- signing keys
- full sensitive customer payloads

Useful fields:

```text
CorrelationId
WalletId
TransferReference
IdempotencyKey hash or safe representation
Outcome
ElapsedMilliseconds
```

If correlation IDs are implemented, propagate one request ID through logs and responses.

---

## 18. Explicit non-goals

Do not spend core implementation time on:

- BVN/NIN verification
- NIBSS integration
- real NIP connectivity
- USSD
- CBN integration
- a production identity provider
- message brokers unless implementing the optional outbox stretch goal
- microservice decomposition
- Kubernetes

The assessment is a ledger correctness exercise, not a complete banking platform.

---

## 19. Stretch goals — only after core acceptance criteria pass

Priority order:

1. health/readiness endpoints
2. structured correlation IDs
3. rate limiting on transfer
4. transactional outbox with `TransferCompleted`

Do not sacrifice concurrency tests, idempotency, Docker reliability, or documentation for stretch goals.

### Implemented stretch goals

All four optional goals were subsequently implemented after the required suite passed:

1. anonymous `/health/live` and SQL/migration-aware `/health/ready` endpoints;
2. bounded `X-Correlation-ID` propagation through response headers, structured log scopes, Problem Details, audits, and outbox payloads;
3. configurable fixed-window rate limiting on transfers, partitioned by authenticated subject;
4. one versioned `TransferCompleted` outbox row committed in the transfer transaction.

The outbox has no dispatcher or broker in this exercise. It stores durable pending events for a future delivery worker. Rate limiting is per API process and protects capacity; SQL transactions remain the financial correctness boundary.

---

## 20. Design trade-offs to be ready to defend

### Pessimistic locking vs optimistic concurrency

This design uses short-lived database locks because the financial invariant is more important than maximizing concurrent writes to the same wallet. Different wallets can still process independently.

### Stored balance vs deriving balance from all ledger rows

The wallet stores a current balance for efficient reads while every mutation is also recorded.

This creates a derived-data consistency responsibility, so wallet balance, transaction rows, audit rows, and idempotency state are committed in one database transaction.

### SQL Server-specific locking

`UPDLOCK`, `HOLDLOCK`, and `sp_getapplock` are SQL Server-specific. This is an intentional trade-off for explicit, testable correctness in the selected datastore.

### No process-local locks

Process-local locks may appear to fix concurrency in development but do not protect a horizontally scaled deployment. Correctness therefore lives at the database boundary.
