# AI Usage

## Overview

AI tools were used as engineering accelerators during the NovaWallet Ledger Service exercise.

My normal workflow is architecture-first: requirements are decomposed, financial invariants and failure cases are defined, unsafe alternatives are challenged, and only then is a detailed implementation prompt given to a coding agent.

For this exercise the workflow was:

```text
requirements
-> AI-assisted design exploration
-> define financial invariants
-> reject naive/unsafe approaches
-> document final architecture
-> create detailed implementation prompt
-> incremental coding-agent implementation
-> review and automated verification
```

Some naive AI-assisted approaches were therefore caught during **design exploration before they became implementation defects**. This is deliberate: in a financial system, preventing an unsafe design from reaching code is preferable to discovering it afterward.

## Tools used

Update the exact tool/model names before final submission so this reflects what was actually used.

| Tool | Usage |
|---|---|
| ChatGPT | requirement decomposition, architecture discussion, concurrency/idempotency review, documentation refinement |
| Coding agent in IDE | incremental implementation from `docs/PROMPT.md`, refactoring, automated-test implementation |
| Additional model/tool if used | second-opinion architecture or code review |

Do not list a tool that was not actually used.

## Prompt example 1 — architecture challenge

> Review the proposed NovaWallet ledger architecture as a senior payments/backend engineer. Do not implement code yet. Try to break it around concurrent debits, transaction isolation, deadlocks, idempotency, duplicate requests, WAT daily transfer limits, partial database failures, audit consistency, and horizontal scaling. Explain the unsafe interleaving and the invariant that should prevent it.

The architecture was refined before implementation rather than delegating these decisions to the coding agent.

## AI-assisted design correction 1 — daily-limit race

### Naive approach considered during exploration

An early AI-assisted design approach treated the daily outbound limit as a validation step before the protected transfer mutation:

```text
1. query today's outbound amount
2. decide whether the new transfer is below the limit
3. later execute the transfer
```

### Why it was unsafe

The check and balance mutation were not protected by the same concurrency boundary.

Example:

```text
daily limit = 100
already sent = 80

request A wants 20
request B wants 20

A reads 80 -> allowed
B reads 80 -> allowed
A commits -> total 100
B commits -> total 120
```

Both requests are valid when viewed independently, but together they violate the server-side limit.

### Correction

The final architecture performs the daily-limit calculation inside the same SQL transaction used for the transfer and only after the source wallet has been locked for mutation.

Concurrent transfers from the same source wallet therefore cannot independently approve themselves using the same stale daily total.

### Verification

The integration suite is required to include a daily-limit contention test where individually valid concurrent requests would exceed the limit when combined. The committed outbound total must never exceed the configured limit.

## AI-assisted design correction 2 — weak concurrency proof

### Naive approach considered during exploration

An initial AI-assisted testing approach ran concurrent transfer requests and primarily asserted that the source balance did not become negative.

Conceptually:

```text
run concurrent requests
await completion
assert source balance >= 0
```

### Why it was insufficient

A non-negative final balance does not prove conservation of money, exact successful-transfer count, ledger consistency, or audit consistency. A system could still lose or duplicate money while satisfying that one assertion.

### Correction

The acceptance test was strengthened to create meaningful contention:

```text
source initial balance = 10,000,000 kobo
destination initial balance = 0
20 concurrent transfers
each transfer = 1,000,000 kobo
```

It must assert:

```text
exactly 10 transfers succeed
exactly 10 fail for insufficient funds
source final balance = 0
destination final balance = 10,000,000
exactly 10 transfer-debit rows
exactly 10 transfer-credit rows
exactly 20 corresponding audit rows
no persisted wallet balance is negative
```

This verifies financial conservation and exact ledger outcomes rather than merely checking that a number never went below zero.

## Why these examples matter

Modern coding models often produce syntactically correct and plausible code. The relevant engineering risk is therefore not only obvious hallucination.

For a financial ledger, generated designs and code must be checked against explicit properties:

```text
no negative balances
no duplicate transfer processing
atomic debit and credit
daily limit cannot be raced
audit records cannot be omitted
money uses integer kobo only
```

AI output is treated as a proposal until those properties are independently verified.


## AI-assisted design decision — validation strategy

During AI-assisted design exploration, simpler validation approaches were considered, including inline controller checks, DataAnnotations, and lightweight format or regex validation.

I chose **FluentValidation** instead as the standard request-validation mechanism for the API.

The decision was architectural rather than cosmetic. I wanted request validation to remain centralized, reusable, and easy to extend as the API grows, instead of allowing validation logic to become scattered across controllers and service methods.

FluentValidation was a better fit because it:

- keeps controllers focused on HTTP orchestration;
- makes validation rules easier to discover and maintain;
- avoids repeated manual `if` checks;
- supports conditional rules and more complex or nested object graphs cleanly;
- provides a consistent validation style across request models;
- integrates with a single RFC 7807 Problem Details error contract.

The boundary is intentional:

```text
FluentValidation
    -> request shape and basic input rules

NovaWallet.Service
    -> stateful and financial business rules

SQL Server
    -> final data-integrity constraints
```

For example, FluentValidation handles:

```text
AmountKobo > 0
required customer ID
customer ID length
required source/destination wallet IDs
source != destination
pagination bounds
```

It does **not** decide:

```text
whether a wallet exists
whether sufficient funds exist
whether the WAT daily limit has been exceeded
whether an idempotency key conflicts with persisted state
whether the current balance can support a transfer
```

Those checks depend on current database state. In a financial system, some of them must also execute inside the transfer transaction so they cannot be invalidated by a concurrent request after validation has already passed.

This separation also reduces the risk of time-of-check/time-of-use errors. A request validator should verify that a transfer request is structurally valid; it should not make a concurrency-sensitive decision about whether money can actually move.

FluentValidation failures are normalized into the same RFC 7807 Problem Details response contract used elsewhere in the API.

## Prompt example 2 — implementation

The coding agent is instructed to read:

```text
README.md
AI_USAGE.md
docs/ARCHITECTURE.md
docs/PROMPT.md
```

before modifying code.

The implementation prompt specifies project responsibilities, persistence constraints, transaction ordering, idempotency semantics, WAT daily-limit handling, JWT and Problem Details behavior, Docker requirements, concurrency tests, and incremental Git commit boundaries.

## Prompt example 3 — adversarial implementation review

> Review the completed implementation against `docs/ARCHITECTURE.md` and `docs/PROMPT.md`. Do not modify code yet. Find any path that could violate a financial invariant. Pay particular attention to transaction boundaries, SQL lock duration, deadlock ordering, same-key idempotency races, daily-limit races, integer overflow, audit consistency, and tests that appear concurrent without actually proving correctness. Give concrete file/method references and a request interleaving for each finding.

## Implementation-stage findings

Add only real findings from the completed implementation here. Examples include a missing database constraint, an accidentally serialized concurrency test, unchecked integer arithmetic, a query outside the intended transaction, or an error response exposing internal details.

Do not invent an implementation-stage bug if none occurs.

## Git usage

The coding agent is instructed to implement in logical phases and create small **local commits** after each verified phase. This creates a reviewable engineering history rather than one large AI-generated commit.

The agent is explicitly instructed **not to push automatically**.

## Final update checklist

Before submission:

```text
[ ] replace the tools table with exact tools/models actually used
[ ] make sure prompt examples reflect prompts actually used
[ ] keep the two design corrections accurately described as design-stage refinements
[ ] add only real implementation-stage findings
[ ] verify README matches the actual code
[ ] verify every test mentioned here exists and passes
[ ] remove stale TODO language
```
