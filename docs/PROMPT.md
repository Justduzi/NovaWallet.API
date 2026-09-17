# NovaWallet Coding Agent Implementation Prompt

## Role

You are implementing the NovaWallet Ledger Service take-home exercise in an existing .NET 8 solution.

Do not treat this as generic CRUD.

This is a financial ledger exercise. Correctness under concurrency, atomicity, durable idempotency, test rigor, and explainability are the primary requirements.

Before writing or changing code:

1. Read `docs/ARCHITECTURE.md` completely.
2. Read `README.md`.
3. Read `AI_USAGE.md`.
4. Inspect the existing solution and project references.
5. Produce a short implementation plan mapping the current repository to the target design.
6. Identify any conflicts between the existing codebase and `docs/ARCHITECTURE.md`.
7. Do not silently change architectural decisions. If a documented decision is impossible or clearly harmful in the existing solution, report it first.

After the plan, implement in small verifiable phases.

---

# 1. Git workflow — implement and commit incrementally

The repository already exists. Do not implement everything and create one giant final commit.

Create small, logical **local commits** as each coherent phase is completed and verified.

Git rules:

- Inspect `git status` before each phase.
- Never commit unrelated pre-existing user changes.
- Stage only files belonging to the completed phase.
- Build/test the phase before committing it.
- Do not commit a knowingly broken phase.
- Use clear commit messages.
- Do not rewrite or squash existing history unless the user asks.
- **Do not push to GitHub automatically.** Create local commits only; the user decides when to push.
- Never commit real secrets, tokens, `.vs`, `bin`, or `obj`.
- If existing user changes cannot be safely separated from agent changes, stop and report the conflict before committing.

Use this implementation/commit sequence unless the existing solution requires a justified adjustment:

### Commit 1 — domain and persistence foundation

Implement domain entities, transaction enums, repository contracts, EF Core DbContext/configuration, constraints, indexes, migrations, and database-level audit immutability.

Verify `dotnet build`.

Suggested commit:

```text
feat: add wallet ledger domain and persistence foundation
```

### Commit 2 — wallet operations and API foundation

Implement wallet creation, balance, credit, statement pagination, DTOs, validation, service/repository wiring, JWT, Swagger bearer setup, and Problem Details foundation.

Verify build and relevant unit tests.

Suggested commit:

```text
feat: add wallet operations authentication and API contracts
```

### Commit 3 — concurrency-safe transfer and idempotency

Implement the SQL transaction, deterministic wallet locking, durable idempotency, canonical request hashing, daily-limit check inside the protected transaction, transaction rows, and audit rows.

Verify build and unit tests.

Suggested commit:

```text
feat: add atomic transfer idempotency and daily limits
```

### Commit 4 — integration and concurrency tests

Implement the SQL Server Testcontainers fixture and the overspend, concurrent idempotency, daily-limit contention, and audit-immutability tests.

Verify `dotnet test`.

Suggested commit:

```text
test: add ledger concurrency and idempotency integration coverage
```

### Commit 5 — Docker and evaluator experience

Implement Dockerfile, Compose, DB readiness/migration startup behavior, evaluator-friendly local JWT flow if chosen, and health/readiness endpoints if time permits.

Verify build, tests, `docker compose config`, and `docker compose up`.

Suggested commit:

```text
chore: add dockerized local environment and service readiness
```

### Commit 6 — final documentation cleanup

Update README and AI_USAGE to match the code that actually exists. Verify commands, ports, JWT instructions, assumptions, and stretch-goal status. Remove stale TODOs.

Suggested commit:

```text
docs: finalize architecture usage and run instructions
```

If a smaller intermediate commit is genuinely useful, make it. Prefer coherent, reviewable history over rigidly forcing unrelated changes together.

---

# 2. Non-negotiable invariants

The completed implementation MUST satisfy all of the following:

- .NET 8.
- SQL Server.
- All money represented as integer kobo:
  - C#: `long`
  - SQL: `BIGINT`
- No `float`, `double`, or monetary `decimal` anywhere in the money path.
- Wallet balance can never be negative.
- Transfer debit and credit are atomic.
- Concurrent transfers from the same wallet cannot double-spend.
- Daily outbound limit is concurrency-safe.
- Transfer endpoint requires `Idempotency-Key`.
- Same key + same payload does not process twice.
- Same key + different payload returns `409 Conflict`.
- Idempotency is durable in SQL Server, not memory.
- Audit records are separate from wallet transaction records.
- Audit log is append-only and database-protected from update/delete.
- All functional endpoints require JWT bearer authentication.
- Errors use RFC 7807 Problem Details.
- Swagger/OpenAPI is enabled.
- `docker compose up` starts the API and database.
- Integration tests exercise real SQL Server concurrency behavior.

Do not substitute an in-memory database for concurrency tests.

---

# 3. Expected projects

Preserve this structure if it already exists:

```text
NovaWallet.Api/
NovaWallet.Domain/
NovaWallet.Repositories/
NovaWallet.Service/
NovaWallet.UnitTests/
NovaWallet.IntegrationTests/
```

If the user's Visual Studio solution uses equivalent names, keep the existing names instead of renaming projects unnecessarily.

Dependency direction should remain clean:

```text
NovaWallet.Domain        -> no infrastructure dependency
NovaWallet.Repositories  -> NovaWallet.Domain
NovaWallet.Service       -> NovaWallet.Domain abstractions
NovaWallet.Api           -> NovaWallet.Service + NovaWallet.Repositories
Test projects            -> required projects
```

Repository interfaces may live in Domain so Services can depend on abstractions while Repository provides implementations.

---

# 4. Domain model

Implement the domain concepts described in `docs/ARCHITECTURE.md`.

Minimum entities:

```text
Wallet
WalletTransaction
AuditLog
IdempotencyRecord
```

Minimum transaction types:

```text
Credit
TransferDebit
TransferCredit
```

Do not expose EF Core entities directly as API request models.

---

# 5. EF Core persistence

Use EF Core 8 with SQL Server.

Create:

- `NovaWalletDbContext`
- entity configurations
- migrations
- indexes
- check constraints

Required constraints:

```text
Wallet.BalanceKobo >= 0
WalletTransaction.AmountKobo > 0
WalletTransaction.BalanceBeforeKobo >= 0
WalletTransaction.BalanceAfterKobo >= 0
Wallet.CustomerId UNIQUE
IdempotencyRecord.IdempotencyKey UNIQUE
```

Required indexes should support:

- wallet statement newest-first query
- daily source debit sum
- transfer reference lookup
- idempotency lookup

Create a SQL migration that protects `AuditLogs` from `UPDATE` and `DELETE`, preferably with a trigger.

Do not rely only on "the application never calls update" for immutability.

---

# 6. API contracts

Implement the following API surface unless equivalent routes already exist:

```http
POST /api/wallets
GET  /api/wallets/{walletId}/balance
POST /api/wallets/{walletId}/credits
POST /api/transfers
GET  /api/wallets/{walletId}/statement?page=1&pageSize=20
```

## Create wallet

Request:

```json
{
  "customerId": "CUST-001"
}
```

Starting balance MUST be zero.

Currency MUST be `NGN`.

## Credit wallet

Request:

```json
{
  "amountKobo": 10000000
}
```

`amountKobo` MUST be a positive `long`.

## Transfer

Header:

```http
Idempotency-Key: example-key
```

Request:

```json
{
  "sourceWalletId": "guid",
  "destinationWalletId": "guid",
  "amountKobo": 1000000
}
```

Reject source == destination.

## Statement

Return newest first with stable ordering.

Include pagination metadata.

Suggested shape:

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 42,
  "items": []
}
```

---

# 7. Transfer implementation — critical section

This phase is the most important part of the exercise.

DO NOT implement financial correctness with:

```csharp
lock (...)
SemaphoreSlim
ConcurrentDictionary<WalletId, SemaphoreSlim>
```

Process-local synchronization is not a correctness mechanism for a horizontally scaled API.

Use a SQL transaction.

## Required transfer sequence

Inside one database transaction:

1. Acquire transaction-scoped `sp_getapplock` for:
   `transfer-idempotency:{IdempotencyKey}`.
2. Read existing idempotency record.
3. Compute/compare canonical request SHA-256 hash.
4. Existing record:
   - same hash -> deserialize and return stored response, with no balance changes.
   - different hash -> raise idempotency conflict.
5. For a new key, load and lock both wallet rows using SQL Server:
   `WITH (UPDLOCK, HOLDLOCK)`.
6. Lock wallet IDs in deterministic sorted order.
7. Verify both wallets exist.
8. Verify source != destination.
9. Check sufficient source funds.
10. Compute current WAT day UTC start/end using injected `TimeProvider`.
11. Sum committed `TransferDebit` amount for the source wallet within that WAT day.
12. Verify current total + requested amount <= configured limit.
13. Update source balance.
14. Update destination balance.
15. Insert source `WalletTransaction`.
16. Insert destination `WalletTransaction`.
17. Insert source `AuditLog`.
18. Insert destination `AuditLog`.
19. Insert `IdempotencyRecord` containing:
    - key
    - request hash
    - HTTP status
    - serialized response body
20. `SaveChanges`.
21. Commit transaction.
22. Return response.

Any failure must roll back the full operation.

Do not call any external HTTP service while the database transaction is open.

---

# 8. Canonical idempotency hash

Do not hash raw JSON.

Use a canonical representation such as:

```text
{sourceWalletId:D lowercase}|{destinationWalletId:D lowercase}|{amountKobo}
```

Encode UTF-8 and SHA-256 it.

Store uppercase or lowercase hex consistently.

Idempotency key rules:

- required
- trim surrounding whitespace
- non-empty
- max length 128

Missing key -> 400.

Existing key with different hash -> 409.

---

# 9. Daily outbound transfer limit

Default configuration:

```json
{
  "WalletOptions": {
    "DailyOutboundLimitKobo": 50000000
  }
}
```

`50,000,000` kobo = `₦500,000`.

Use injected `.NET TimeProvider`.

WAT is UTC+01:00.

Store timestamps in UTC.

Calculate current WAT midnight and next midnight, then convert both to UTC.

Query:

```text
WalletId == source
Type == TransferDebit
CreatedAtUtc >= startUtc
CreatedAtUtc < endUtc
```

The query MUST execute while the source wallet row is locked inside the transfer transaction.

Do not:

1. query daily usage,
2. leave the transaction/concurrency boundary,
3. later process the transfer.

That has a race condition.

---

# 10. Credit implementation

Credit is also a protected balance mutation.

Inside one DB transaction:

1. lock wallet with `UPDLOCK, HOLDLOCK`
2. read old balance
3. calculate new balance using checked integer arithmetic
4. update balance
5. add `WalletTransaction`
6. add `AuditLog`
7. save
8. commit

Use `checked` arithmetic where overflow is possible.

---

# 11. JWT

Configure ASP.NET Core JWT bearer authentication.

Use configuration:

```text
Jwt:Issuer
Jwt:Audience
Jwt:SigningKey
```

All wallet/transfer/statement endpoints require `[Authorize]`.

Read the authenticated subject claim (`sub`) and use it as `ActorSubject` in audit entries where available.

Swagger must support Bearer token entry.

A small DEVELOPMENT-ONLY token endpoint is acceptable for evaluator convenience.

If implemented:

- only map it in `Development`
- clearly label it as non-production
- never expose it in Production

---

# 12. Problem Details

Use centralized exception handling / `IExceptionHandler` or equivalent.

Return RFC 7807 responses.

Required mapping:

```text
validation                     -> 400
wallet not found               -> 404
duplicate customer             -> 409
idempotency payload conflict   -> 409
insufficient funds             -> 422
daily limit exceeded           -> 422
unexpected exception           -> 500
```

Add machine-readable error code extension values.

Never return SQL exception text or stack traces to the client.

---

# 13. Automated tests

Use xUnit.

Use real SQL Server for integration tests.

Preferred:

```text
Microsoft.AspNetCore.Mvc.Testing
Testcontainers.MsSql
```

Apply migrations at test startup.

Do not use EF Core InMemory to claim concurrency correctness.

## 12.1 Required overspend concurrency test

Arrange:

```text
source initial balance = 10,000,000 kobo
destination = 0
20 concurrent requests
each request = 1,000,000 kobo
each request gets a different Idempotency-Key
```

Act:

- release all 20 requests as close together as practical using `Task.WhenAll`
- avoid accidentally serializing them in test setup

Assert:

```text
10 succeed
10 fail for insufficient funds
source balance = 0
destination balance = 10,000,000
no stored balance is negative
10 transfer-debit rows
10 transfer-credit rows
20 transfer audit rows
```

If HTTP semantics make exact success/error counts differ for a justified reason, explain it. Financial totals and no-double-spend invariants are non-negotiable.

## 12.2 Required concurrent idempotency test

Arrange one funded source and destination.

Send the exact same transfer many times concurrently using the SAME idempotency key.

Assert:

```text
money moved once
one debit row
one credit row
one idempotency record
same transfer reference/result returned on replays
```

Then send a different payload using the same key.

Assert:

```text
409 Conflict
no additional balance mutation
```

## 12.3 Daily-limit concurrency test

Configure a deliberately small daily limit.

Send multiple concurrent transfers that would exceed the limit if each checked a stale total.

Assert committed outbound amount never exceeds the limit.

## 12.4 Audit immutability test

Attempt direct SQL:

```sql
UPDATE AuditLogs ...
```

and/or:

```sql
DELETE FROM AuditLogs ...
```

Assert SQL Server rejects the modification.

## 12.5 Unit tests

Add focused unit tests for:

- canonical request hash
- WAT date boundaries
- validation
- any pure daily-limit calculation

---

# 14. Docker

Create:

```text
Dockerfile
docker-compose.yml
.dockerignore
```

`docker compose up` must be sufficient to start:

- SQL Server
- API

API should retry DB connection/migration on startup because `depends_on` alone does not mean SQL Server is ready.

Suggested local URL:

```text
http://localhost:8080
http://localhost:8080/swagger
```

Use environment-overridable development defaults.

Never claim the default local Docker password/JWT signing key is production-safe.

---

# 15. README verification

After implementation, UPDATE `README.md`.

Do not leave architectural claims in README that the code does not actually satisfy.

README must contain:

- prerequisites
- one-command startup
- service URLs
- how to obtain/use a development JWT
- sample curl requests
- how to run tests
- architecture summary
- concurrency strategy
- idempotency behavior
- WAT daily-limit behavior
- security notes
- trade-offs
- assumptions
- stretch goals implemented, if any

---

# 16. AI usage evidence

Do not fabricate an AI error.

While implementing, keep brief notes of:

- design suggestions you rejected
- generated code you corrected
- tests you strengthened
- missing edge cases found during review

At the end, update `AI_USAGE.md` with at least one ACTUAL observed case.

Two likely classes of issue to watch for during review are:

### A. Daily-limit race

A naive implementation may query the daily total outside the wallet lock/transfer transaction and later perform the debit.

If this occurs, record it in `AI_USAGE.md` and fix it by moving the limit check inside the locked transaction.

### B. Weak concurrency proof

A generated test may race only two requests or merely assert that no exception occurred.

If this occurs, record it and replace it with a meaningful contention test asserting exact financial invariants and final balances.

These are examples to inspect for, not events to claim unless they actually happen.

---

# 17. Code quality requirements

Use:

- async APIs end-to-end
- cancellation tokens where appropriate
- dependency injection
- options/configuration pattern
- UTC timestamps
- `TimeProvider`
- `ILogger`
- descriptive types and method names

Avoid:

- generic "Manager" classes with many responsibilities
- controllers containing financial business logic
- static mutable state
- magic numbers
- silent catch blocks
- process-local locking for correctness
- float/double/decimal money
- fire-and-forget financial writes

---

# 18. Stretch goals

Only implement after all required tests pass.

Preferred order:

1. readiness/liveness health checks
2. correlation/trace IDs
3. ASP.NET Core rate limiter on transfer endpoint
4. transactional outbox and `TransferCompleted` event

If an outbox is implemented, the outbox row must be inserted in the SAME database transaction as the transfer.

Do not add Kafka/RabbitMQ merely to display complexity.

---

# 19. Final self-review

Before declaring the task complete, answer each item with evidence from the code/tests:

```text
[ ] Can two different transfer requests overspend one wallet?
[ ] Can two instances of the API preserve the same guarantee?
[ ] Can the same idempotency key move money twice?
[ ] Can the same key be reused for another payload?
[ ] Can concurrent requests bypass the daily limit?
[ ] Can source debit commit without destination credit?
[ ] Can a committed mutation exist without an audit record?
[ ] Can audit history be updated/deleted?
[ ] Is every money value an integer kobo value?
[ ] Are JWT-protected endpoints actually protected?
[ ] Are unexpected errors returned as Problem Details?
[ ] Does docker compose start from a clean machine with Docker?
[ ] Does the concurrency test use real SQL Server?
[ ] Does README match the implemented system?
[ ] Does AI_USAGE describe actual AI use truthfully?
```

Run:

```bash
dotnet build
dotnet test
docker compose config
```

Then start the full system with:

```bash
docker compose up
```

Verify Swagger and the core workflow manually.

Report any remaining TODOs rather than hiding them.
