# AI usage and review evidence

## Scope and tools

The implementation in this checkout was produced with the Codex coding agent, using the repository workspace, PowerShell, .NET build/test tools, EF migration generation, Git and Docker. No second review model or delegated agent was used in this implementation session. Generated code was reviewed against the invariants in `docs/ARCHITECTURE.md` and exercised with automated tests.

The pre-existing design documents describe earlier ChatGPT-assisted architecture discussions, including rejecting a daily-limit check outside the wallet lock and rejecting weak concurrency assertions. Those are supplied design-history accounts, not implementation defects observed in this session. They are not presented below as newly discovered bugs.

## Actual instruction

The user asked:

> read the prompt.md file in docs and implement

The agent read `docs/PROMPT.md`, `docs/ARCHITECTURE.md`, the original README and original AI_USAGE before editing. The implementation followed the documented domain/repository/service/API dependency direction, SQL Server locking strategy, FluentValidation choice and phased local commit workflow. No push was performed.

## Observed correction: generated validation tests did not compile

The first generated validation tests used target-typed constructor calls such as:

```csharp
new CreditValidator().Validate(new(amount))
```

The compiler reported CS0121 because FluentValidation exposes both `Validate(CreditRequest)` and `Validate(ValidationContext<CreditRequest>)`. The generated shorthand was ambiguous.

The tests were corrected to construct explicit request types, for example `new CreditRequest(amount)`. The same correction was applied to transfer, statement, customer and idempotency-key tests. The subsequent test run passed all 13 validation tests. After adding canonical-hash, WAT-boundary and limit-arithmetic tests, all 24 unit cases passed.

This is an actual generated-code correction caught by compilation, rather than an invented financial race.

## Review finding: key equality must match the locking mechanism

While writing persistence, the agent identified that SQL Server's usual case-insensitive text comparison would not match the case-sensitive application-lock resource. Different spellings of a key could acquire distinct locks but collide in a unique database index.

The implementation explicitly sets `Latin1_General_100_BIN2` on `IdempotencyKey`, trims keys before locking and storage, and documents case-sensitive keys. `CaseDistinctIdempotencyKeysAreDistinctInBothLockAndDatabase` exercises this choice using simultaneous `KEY` and `key` transfers. This was a design edge case resolved during implementation, not a claim that a failing runtime test was observed first.

## Review finding: triggered tables need compatible EF inserts

The audit migration adds an `INSTEAD OF UPDATE, DELETE` trigger. EF SQL Server output generation was explicitly disabled for this table with `UseSqlOutputClause(false)` and trigger metadata was configured. This avoids generating an incompatible direct OUTPUT clause on a triggered table. Real-SQL credit/transfer tests exercise audit inserts, while the immutability test attempts direct SQL UPDATE and DELETE.

## Tests strengthened beyond the minimum

The overspend scenario sends 20 requests through two independently constructed API hosts sharing one SQL database. A start gate releases the tasks together. It asserts exact success/error counts, final balances, debit/credit counts, audit counts, idempotency counts and nonnegative stored histories.

Additional tests were written to check:

- exact stored JSON replay across simultaneous hosts and a fresh host;
- multiple destination wallets competing for one daily source limit;
- WAT midnight at the real SQL query boundary with injected time;
- an audit INSERT failure rolling back balances, ledger and idempotency;
- destination/credit overflow leaving persisted balances unchanged;
- opposing transfers interleaved with credits;
- concurrent customer uniqueness and direct database check constraints;
- authentication without replacing JWT middleware with a test authentication handler;
- stable statement pagination when all timestamps tie.

Tests use SQL Server Testcontainers and migrations, not EF InMemory. Database-unavailable errors are test failures, not skipped concurrency checks.

## UTC timestamp materialization

Review identified that SQL datetime2 does not preserve DateTime.Kind. Persistence now marks materialized timestamps as UTC, and the tied-timestamp statement test asserts DateTimeKind.Utc after the HTTP round trip. This corrects ambiguous timestamp serialization without changing stored clock values.

## Workflow corrections and environment observations

- The API project is at repository root. Its default source/content globs were narrowed so nested projects and their build artifacts are not compiled into the API.
- The original `.slnx` was converted to `.sln` to support the .NET 8 SDK without requiring a newer SDK solely to parse the solution.
- An attempted rebuild while the Windows integration test process was still waiting for its first SQL image download hit locked assembly files. Builds and test runs were then sequenced to avoid writing to binaries in use.
- Docker Desktop initially was stopped; it was started with user-approved tool execution. NuGet access, Docker access and local commits required sandbox escalation. Local test credentials are explicitly development-only defaults.
- Container download time is not treated as a passed integration test. Verification outcomes are recorded separately in [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Implementation choices and limits

Financial state checks remain inside the SQL transaction and wallet locks. No daily-limit race was introduced and then claimed as fixed. No process-local synchronization is used as a financial correctness mechanism.

The replay path returns persisted status and JSON directly instead of deserializing and reserializing, preserving the original response exactly. Checked arithmetic rejects unsupported balances; daily-limit comparison subtracts from the limit to avoid overflow.

The implementation uses trusted-operator JWT authorization, not per-customer ownership authorization. Credits are not idempotent. Audit DML protection does not protect against an administrator changing schema. The README states these limits, along with production migration/credential assumptions.

## Stretch-goal review

The later user instruction was to push the completed core implementation, implement every optional stretch goal, commit, and push again. The core six commits were pushed first. The stretch implementation added readiness/liveness endpoints, bounded correlation IDs, per-subject transfer rate limiting, and a versioned `TransferCompleted` SQL outbox row.

Review focused on preserving financial behavior around the additions:

- correlation state is scoped per request, with concurrent tests proving IDs do not cross requests;
- the rate limiter runs after authentication and before controller/service execution, and rejection tests assert no balance, audit, idempotency, or outbox side effects;
- outbox insertion occurs before the existing `SaveChanges` and commit in the transfer transaction;
- an injected outbox constraint failure proves balances, ledger, audit, idempotency, and outbox all roll back and the same idempotency key remains reusable;
- replays return the persisted response without inserting a second event;
- readiness detects both an unavailable database and a deliberately removed migration-history row, while liveness remains independent of SQL Server.

The outbox is deliberately a durable pending-event store, not a claim of delivery. No broker or dispatcher was added merely to display complexity.
