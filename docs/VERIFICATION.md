# Acceptance evidence

This file maps the implementation prompt's self-review questions to executable checks. Final command outcomes are recorded after execution; source-level reasoning alone is not a claimed concurrency-test pass.

| Question | Implementation and test evidence |
|---|---|
| Can two different transfers overspend one wallet? | `TransferService.TransferAsync` holds wallet update locks through commit. `TwentyTransfersAcrossTwoInstancesCannotOverspend` asserts 10 successes, 10 insufficient-funds errors, exact balances and ledger/audit totals. |
| Can two API instances preserve the guarantee? | Locks live in SQL Server. The overspend and idempotency tests alternate between two independent API hosts using one database. |
| Can the same key move money twice? | Transaction-owned `sp_getapplock`, unique durable key, and stored response. Concurrent replay test checks identical JSON, one debit, one credit, one record, and replay from a fresh host. |
| Can the key be reused for another payload? | Canonical request hash comparison returns 409; replay test checks conflict without another mutation. Binary SQL collation matches case-sensitive lock resources. |
| Can concurrency bypass the daily limit? | Source lock is acquired before the WAT debit sum. `ConcurrentDailyLimitCannotBeBypassed` checks exactly three successes and the combined destination balances under a small limit. |
| Can debit commit without credit? | One SQL transaction includes both. Audit-insert failure test and destination-overflow test verify rollback of both balances and transfer history. |
| Can a committed application mutation lack an audit row? | `RecordMutation` always adds both entries in the transaction. Failure-injection test proves audit insertion failure rolls back the mutation. |
| Can audit history be updated/deleted? | Migration trigger rejects UPDATE/DELETE with SQL error 51000. The immutability test executes both operations directly. Privileged DDL/administrative tampering is outside this guarantee. |
| Is all money integer kobo? | Domain entities, DTOs and arithmetic use `long`; migration money columns are `bigint`; fractional JSON amounts are rejected. Overflow tests protect the supported range. |
| Are JWT endpoints actually protected? | Real JWT bearer middleware with issuer/audience/key/lifetime/subject checks; integration test checks anonymous access to all functional routes, invalid tokens and absence of the development issuer in Production. |
| Are unexpected errors Problem Details? | Central `LedgerExceptionHandler`; injected SQL failure asserts status 500, content type, stable code and absence of the injected SQL message. |
| Does Compose start from a clean database? | Dockerfile, database health gate, bounded migration startup retry, and `scripts/smoke.ps1` cover container startup and the core authenticated workflow. Execution result is recorded below. |
| Is concurrency tested on real SQL Server? | `SqlServerFixture` starts a SQL Server 2022 Testcontainer and applies EF migrations to a distinct database per test. No InMemory provider or automatic skip is used. |
| Does README match the implementation? | Exact routes, settings, ports, trusted-operator assumptions, non-idempotent credits and deployment limitations are documented. |
| Is AI usage described truthfully? | `AI_USAGE.md` separates supplied design history from observed compiler/review/workflow corrections. |

Additional coverage: SQL check constraints, concurrent customer uniqueness, opposing transfers interleaved with credits, pagination with tied timestamps, UTC timestamp materialization, WAT midnight, canonical hash culture independence, health semantics, correlation isolation, rate limiting, and outbox atomicity.

## Execution record

Verified on 2026-09-17 in this Windows workspace using Docker Desktop Linux containers:

- `dotnet build --no-restore --disable-build-servers -m:1 -v:minimal`: passed with zero warnings and zero errors.
- Core verification before stretch work: **24 unit tests and 14 SQL Server integration tests passed**, none skipped.
- Stretch verification: `dotnet test NovaWallet.API.sln --disable-build-servers -m:1 -v:minimal` passed **31 unit tests and 23 SQL Server integration tests**, none skipped.
- `dotnet ef migrations has-pending-model-changes --project NovaWallet.Repositories --startup-project NovaWallet.Repositories --no-build`: no model changes since the checked-in migration.
- `docker compose config --quiet`: passed.
- `docker compose up --build -d --wait --wait-timeout 180`: passed, creating a new database volume and starting SQL Server and the .NET 8 API. Release publishing inside the SDK 8 container succeeded.
- `docker compose up --build -d --wait --wait-timeout 180`: passed after stretch work; startup applied `AddTransferOutbox` to the existing database volume and both services reached their expected running/healthy state.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke.ps1`: passed against port 8080 after stretch work, including liveness, readiness, response correlation, authenticated create/credit/transfer/replay, exact balances, statements and OpenAPI JSON.
- Live SQL verification after the smoke run found one `TransferCompleted` schema-v1 outbox row for its one new transfer, with one distinct transfer reference, the supplied correlation ID and null `PublishedAtUtc`.
- Container JSON logs include the correlation ID and W3C trace ID in structured scopes.
- Swagger UI at `/swagger/index.html`: HTTP 200.

The PowerShell workflow was executed. The Bash/curl examples in README were reviewed against the same contracts, but are not claimed as independently executed shell commands.

## Scope limits

SQL locking protects the financial operations across application instances; it does not replace production authorization or schema-management controls. Tests instantiate independent API hosts in one test process, rather than asserting a multi-machine deployment was exercised. Rate limiting is per API process. The outbox stores pending events transactionally but has no delivery worker or external broker in this exercise.
