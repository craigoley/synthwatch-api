# Why the gates exist — `craigoley/synthwatch-api`

**This file exists to stop a new team deleting the CI gates as friction.**

16 workflows, 18 jobs. Each exists because something broke — and in this repo the characteristic failure
is **a gate that was green while asserting nothing**. Two of the gates below spent their entire lives in
that state before being fixed. Read the incident before you decide a check is noise.

Companion documents: [`../../docs/handover/`](.) and the runner's
[`GATES.md`](https://github.com/craigoley/synthwatch/blob/main/docs/handover/GATES.md) — the runner is
where the shared PR-holding procedure and the auto-merge re-arm table live.

---

## How to read the ranking

| Rank | Meaning | Safe to disable under pressure? |
|---|---|---|
| **P0 — LOAD-BEARING** | Removing it re-opens a specific, dated incident. | **No.** |
| **P1 — LOAD-BEARING** | Guards a class that has bitten, but quieter (a silent gap, not an outage). | Only with a named owner and a same-day re-enable. |
| **P2 — NICE-TO-HAVE** | Real value; the failure is recoverable and visible. | Yes, temporarily. |
| **ADVISORY** | `continue-on-error` — cannot block by design. | Nothing to relax. |

**Branch protection:** required status checks = **`ci-gate` only**; required approving reviews = **0**.
`ci-gate` aggregates everything else, and its `REQUIRED` list here is unusually long (8 names) — deleting
a workflow silently removes it from that aggregate.

---

## The gates

### `ci-gate` — the aggregator · **P0**

**Asserts:** every other check reached a terminal state and none failed; the 8 names in `REQUIRED` must
additionally **register** (fail-closed — a check that never appears is a failure, never a silent pass).

`REQUIRED` here: `Test (xUnit + Postgres service)`, `Claude review`, `Trace-signals golden parity`,
`Schema parity (fixture vs runner migrations)`, `Runner column parity (fixture vs runner schema)`,
`Build (warnings as errors)`, `Grant coverage (MI roles vs Bicep)`, `Grant coverage (Postgres table grants)`.

**Same trap as the runner:** `Scan` is **advisory by its own workflow's design** (`continue-on-error:
true`) and is deliberately **not** in `REQUIRED` — requiring a job that can never go red asserts nothing
while looking like a gating control.

**Relaxing it:** don't. It is the only required check.

---

### `assert-tests-ran.py` (inside `Test (xUnit + Postgres service)`) — **P0**

**Asserts:** the DB-backed tests (`[Collection("postgres")]`) actually **ran**, and none of them skipped.

**What went wrong — a green suite hiding 113 skipped tests.** The DB tests call
`Skip.IfNot(_pg.Available, …)`, so with no reachable Postgres they **skip and the suite still reports
GREEN**. Measured on the fallback path (`DATABASE_URL` unset, no Docker):

| Scenario | Result |
|---|---|
| Run 1, persistent DB | **508 passed, 0 skipped** |
| Run 2, same DB | 508 passed, 0 skipped |
| Mutant: reset disabled, run 2 | 127 failed ← the reset is load-bearing |
| **`DATABASE_URL` unset, no Docker** | 395 passed, **113 skipped, no error** |

The trigger anecdote is worse than the number: **four DB-backed tests in #278 were written, reviewed and
pushed having never once executed.** They skipped locally, were reported as "3 passed, 4 skipped" as
though clean, and CI was their first real run — where **all four failed**.

**★ Why it is a real XML parse and not a grep.** The previous inline guard was broken **three ways at
once** and had never executed past its first line:

1. **A TRX `<Counters>` element has no `skipped` attribute.** The schema is
   `total/executed/passed/failed/…/notExecuted/…` — skips land in **`notExecuted`**. The grep matched
   nothing, exited 1, and `set -euo pipefail` killed the step *before* the echo.
2. `| head -1` is the banned **fail-open SIGPIPE** antipattern (see `sigpipe-grep` below).
3. **`notExecuted != 0` is the wrong predicate anyway** — `TraceSignalsGoldenParityTests` skips 2 tests
   *by design* in that job. A whole-suite "zero skips" rule is permanently red and would have been
   disabled within a day.

★ **And `notExecuted` can read 0 while tests skip** — so a counter-based skip gate is **fail-open**, the
exact failure such a gate exists to prevent. The predicate is therefore scoped to the classes the guard is
about: DB-backed tests must have run and none may be skipped. Skips elsewhere are reported, not fatal.

**It has a must-go-red self-test** (`assert-tests-ran.selftest.sh`) over fixed TRX fixtures — *"a guard
that can only ever go red is as useless as one that can only ever go green."*

**Relaxing it:** never. Without it, "tests passed" is not evidence tests ran.

---

### `Schema parity (fixture vs runner migrations)` + `Runner column parity` — **P0**

**Asserts:** (a) every table mapped in `Data/SynthWatchDbContext.cs` exists in the test fixture; (b) for
tables in **both** the runner migrations and the fixture, the fixture carries every column/constraint the
runner defines.

**What went wrong:** the fixture drifting from the runner-owned schema showed up as a **silent coverage
hole** (#129 `reconcile_apply_plan`) and as a **prod 500** (#153, a lagging `infra_error` CHECK).

**Design detail worth preserving:** neither assertion ever diffs the fixture **file** — ~42% of it is
hand-appended incident-memory blocks, and regenerating would clobber them. The self-test proves the
catalog-diff engine genuinely goes red on a dropped column.

**Cross-repo coupling to know about:** this gate reads runner `main` **live**, which is what gives it
teeth — and also why a runner schema change can freeze *this* repo's CI on an unrelated PR. The runner
side has a pre-flight (`Will this freeze synthwatch-api?`) that warns the person who breaks it. It has
fired three times: `countable_run`, `retry_count`, `audit_check_location_change`.

**Relaxing it:** no.

---

### `Grant coverage (MI roles vs Bicep)` + `Grant coverage (Postgres table grants)` — **P0**

**Asserts, across both grant planes:**
- Azure RBAC the MI is **granted** in `infra/main.bicep` vs **required** in `infra/required-grants.json`;
- every Postgres table the `synthwatch-api` role **writes** has its `GRANT` in the **runner repo's**
  migrations (∪ `opsBaseline`).

**What went wrong:** the missing-grant class surfaced only as **runtime 500s** — dismiss-500, approve-500,
reconcile, AOAI, email. Missing `DELETE` (#0044) and missing `UPDATE` (#0053) both reached production.

**★ Why CI cannot catch it any other way:** CI and the api's Testcontainers run as **SUPERUSER with no
`synthwatch-api` role**, so a missing `GRANT` is invisible to every test. Only a static manifest-vs-code
comparison can see it. Runs on **every** PR (no path filter): a PR adding an endpoint that needs a new
role must update both the manifest and Bicep even if it never touches `infra/`.

**Known limitation, stated honestly:** the coverage gate is **writes-only** and a **pure static parse**.
It never creates the role or asserts real grants, so a missing **SELECT** grant is still invisible until a
prod 500. *Do not read "the grant gate is green" as proof a grant exists.*

**Relaxing it:** no.

---

### `Trace-signals golden parity` — **P0**

**Asserts:** the C# `TraceExtractor` and the runner's `traceSignals.ts` produce **byte-identical** output
for a shared golden fixture.

**What went wrong:** runner ↔ C# extractor divergence is a **recurring** class — #169 through #172, where
#172 completed what #171 only half-fixed. Two implementations of one wire format drift silently; the
shared golden fixture is the only thing that couples them.

**Relaxing it:** no — and if you change one extractor, update the **shared fixture**, never one side.

---

### `Build (warnings as errors)` — **P1**

**Asserts:** the solution builds with warnings escalated to errors. Cheap, and the first line of defence
for a compiled language.

---

### `sigpipe-grep` (shell-safety) — **P1**

**Asserts:** no fail-open SIGPIPE antipattern in tracked `*.sh` or workflow `run:` blocks.

**The bug — five instances org-wide, and shellcheck/actionlint miss all of them.** A producer piped into
an **early-closing** consumer under `set -o pipefail`:

```bash
printf '%s' "$BIG" | grep -q PATTERN   # grep -q exits on the FIRST match
cmd "$BIG"         | head -N           # head exits after N lines
```

The still-writing producer takes **SIGPIPE (141)** the instant the consumer closes the pipe; `pipefail`
makes the whole pipeline exit 141 — **even though grep matched**. A guard built on that exit
(`if ! …`, `… || continue`) **inverts**: a match reads as "no match", silently flipping BLOCK→PASS.

★ **It is input-size-dependent** — it passes every small-input test and fails only in prod on a large
input. That is why it needs a scanner rather than a test.

**Where it actually bit:** runner `#155` (SHA-tag membership), `#279` (CORS verify printed *"template
declares no blob CORS"* while CORS **was** declared and live), `#283` (the `SHA_OVERRIDE` sibling — the
#155 fix left two siblings unswept). It is also defect #2 in the `assert-tests-ran.py` story above.

**The fix is a here-string**, not `|| true` (which hides it): `if ! grep -q P <<<"$BIG"; then`.
A reviewed-safe line may opt out with a trailing `# sigpipe-ok`.

**Relaxing it:** P1 — it is a sub-second static scan guarding a bug class that is invisible to review.

---

### `AuthGatesDocParityTests` (doc-parity tripwire, in the test suite) — **P2**

**Asserts:** the auth-gates documentation lists exactly the gates the code implements, in both
directions. The runner has the mirror of this (`statusTaxonomyDoc.test.ts`).

**Scope is deliberately structural only** — it does not gate prose about what each gate *means*, which is
semantic and unenforceable.

**Why P2:** a stale doc misleads a person; it does not break prod. But it is nearly free, and it is what
keeps the doc from becoming fiction.

---

### `Claude review` — **P1 (and a merge-hold lever)**

In `REQUIRED`. See **Holding a PR** below — disabling it stalls `ci-gate` for its full deadline.

---

### `Analyze (…)` CodeQL · `Review dependency changes` · `dotnet list package --vulnerable` — **P1/P2**

CodeQL and dependency-review gate. The vulnerable-package scan is P2 — advisory in practice, useful for
supply-chain drift.

### `Scan` (semgrep) — **ADVISORY**

`continue-on-error: true`; cannot block. **`# nosemgrep` does not clear the GitHub code-scanning check** —
satisfy the rule or dismiss the alert in the Security tab.

### `heal` · `Auto-merge (trusted authors)` — **ORCHESTRATION**

Never blocking, by design. A failed remediation bot must not block a good PR.

---

## ★ Holding a PR open

Identical hazard to the runner, and the full re-arm table lives there. The short version for this repo:

```bash
gh workflow disable "Claude review" -R craigoley/synthwatch-api
gh workflow disable "Auto-merge"    -R craigoley/synthwatch-api
# …then re-enable BOTH afterwards…
```

- **`gh pr view --json autoMergeRequest` is point-in-time, not durable.** A `null` is a snapshot, not a
  guarantee.
- Auto-merge here re-arms on `pull_request: [opened, synchronize, reopened, ready_for_review]` — **any new
  commit re-arms it**.
- **`Claude review` is in `REQUIRED` and `ci-gate` is fail-closed on a check that never registers.**
  Disabling it means `ci-gate` waits its full **15-minute** deadline and then **fails**. That red is a
  timeout, not a real failure — re-enable and **re-run `ci-gate`**; it will not clear on its own.

---

## What is safe to relax under real pressure

1. **`Scan`** — already advisory.
2. **`dotnet list package --vulnerable`** (P2) — a day without it is recoverable.
3. **doc-parity tripwire** (P2) — misleads a person, not prod.
4. **`Build (warnings as errors)`** (P1) — only if you accept warnings landing on `main`.

**Never:** `ci-gate`, `assert-tests-ran.py`, both `Schema parity` jobs, both `Grant coverage` jobs,
`Trace-signals golden parity`.

Three of those exist specifically because something was **green while asserting nothing** — the grant
gates, the schema fixture, and the skip guard. They are the least intuitive to keep and the most
expensive to lose.
