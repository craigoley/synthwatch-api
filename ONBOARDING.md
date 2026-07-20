# Onboarding — `synthwatch-api`

> _2026-07-15 · prose with **no automated check**. This doc **points**; it does not copy. If a doc and the code
> disagree, the code wins and the gate proves it._

## 1. What this repo is

A **read-mostly** C# **Azure Functions** (.NET 10 isolated worker) HTTP API over the runner-owned Postgres DB,
authenticating with a **managed identity** (no password). It is the dashboard's only backend. **It does NOT own
the schema — the runner does**; this repo maps the existing tables and writes only its own operator/config
tables. Its place in the 4-repo system + the handover plan:
**[TRANSITION.md](https://github.com/craigoley/synthwatch/blob/main/TRANSITION.md)** (in the runner repo).

## 2. First hour (from a clean clone)

### Route A — the devcontainer (needs Docker)

The **#318 devcontainer lives in the runner repo and builds + tests this repo too.** Clone both as siblings:

```bash
git clone https://github.com/craigoley/synthwatch
git clone https://github.com/craigoley/synthwatch-api        # sibling of the runner
cd synthwatch
docker compose -f .devcontainer/docker-compose.yml up -d
docker compose -f .devcontainer/docker-compose.yml exec app bash .devcontainer/postCreate.sh
docker compose -f .devcontainer/docker-compose.yml exec app bash .devcontainer/verify.sh b   # step (b) = this repo's dotnet test (Testcontainers)
# or directly, from synthwatch-api/:  dotnet test tests/SynthWatch.Api.Tests/
```

### Route B — local, no Docker at all

Two pieces, and **you need both**. Skip either one and the suite still exits 0, which is the trap this
section exists to prevent — see "reading the result" below.

**1. The .NET SDK.** Without it there is no `dotnet`, so the suite cannot run at all — `dotnet test` is
`command not found`. The repo targets **net10.0**; install the 10.0 channel and put it on `PATH`:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"     # add to ~/.zshrc to persist
dotnet --version                       # expect 10.0.x
```

**2. A Postgres for `DATABASE_URL`.** The DB-backed tests need a real postgres:16. Testcontainers is only a
fallback and needs a Docker daemon, so on this route you supply the DB yourself:

Use a **throwaway cluster on a non-default port** — its own data dir, its own port, started by hand. Not
`brew services start postgresql@16`; the reason is structural and worth understanding before you run this:

> ⚠️ **`DATABASE_URL` must point at a THROWAWAY database.** `PostgresFixture` runs
> `DROP SCHEMA public CASCADE` on this path before seeding — it has to, or a second run stacks seed rows on
> the first. Point it at a DB you care about and it will be emptied. Never reuse your everyday local DB, and
> never point it at anything shared.
>
> **This is why the throwaway cluster, not the brew service.** `brew services start postgresql@16` runs a
> shared server on the default **:5432** — the same server your other projects use — so one stale
> `DATABASE_URL`, one typo'd database name, and `DROP SCHEMA public CASCADE` lands on real work. A separate
> cluster on **:55432** has its own data directory and contains nothing but `synthwatch_test` and the
> templates, so it **cannot reach an everyday database even if the URL is wrong**. Isolation by
> construction beats remembering to be careful.

```bash
brew install postgresql@16
export PGBIN="$(brew --prefix postgresql@16)/bin"       # keg-only: NOT on PATH by default

# A throwaway cluster: own data dir, own port, isolated from any Postgres you already run.
export PGDATA="$HOME/.synthwatch-testdb"
export PGPORT=55432
"$PGBIN/initdb" -D "$PGDATA" -U postgres --auth=trust
"$PGBIN/pg_ctl" -D "$PGDATA" -l "$PGDATA/server.log" \
    -o "-p $PGPORT -c listen_addresses=127.0.0.1" start
"$PGBIN/createdb" -h 127.0.0.1 -p "$PGPORT" -U postgres synthwatch_test

export DATABASE_URL="postgres://postgres@127.0.0.1:$PGPORT/synthwatch_test"
dotnet test tests/SynthWatch.Api.Tests/

# stop it when you're done — the data dir persists, so `pg_ctl … start` resumes it next time.
# To reset from scratch: stop, then `rm -rf "$PGDATA"`, then re-run initdb.
"$PGBIN/pg_ctl" -D "$PGDATA" stop
```

★ **This block is the tested path, not a plausible one** — it was run start-to-finish against postgres
**16.14** on macOS/arm64 with no Docker, reaching `Passed: 524, Skipped: 0`. `--auth=trust` is safe here
*because* of `listen_addresses=127.0.0.1`: the cluster accepts loopback connections only.

### ★ Reading the result — `Skipped: 0` is the tell

**A run with skips is not a clean run.** The DB-backed tests `Skip.IfNot(...)` when no Postgres was
resolved, so the suite reports **green while barely testing anything**. Measured on `main` (1c939f1),
identical command, only `DATABASE_URL` differing:

| state | result | verdict |
|---|---|---|
| no SDK | `dotnet: command not found` | can't run at all |
| SDK, **no** `DATABASE_URL`, no Docker | exit **0** — `Passed: 405, Skipped: 119, Total: 524` | **green, but 119 never ran** |
| SDK + `DATABASE_URL` | exit **0** — `Passed: 524, Skipped: 0, Total: 524` | the only clean run |

Narrowed to the DB-backed class it is starker: without `DATABASE_URL`, `IntegrationTests` runs **14 of 127**
and still exits 0. So check the count, not the colour — **if `Skipped:` is not `0`, you have not run the
suite**, and CI enforces exactly this (`scripts/assert-tests-ran.py`, from #279).

A `DATABASE_URL` that is set but **unreachable** is a hard failure by design (exit 1, all 127 fail) — it is
never downgraded to a skip. That contract is pinned by `PostgresFixtureContractTests` (#281).

Then: trivial change → branch → push → **open a PR** → CI green → **auto-merges** (`auto-merge.yml`).

## 3. ★ The one thing that will bite you day one

This README has **no dedicated landmine box** (unlike the runner + monitors — a candidate to add). The two
real day-one bites:

- **You don't own the schema.** Touching a table the runner owns means a runner PR + a paired fixture bump, or
  this repo's **schema-parity gate freezes** (see `CLAUDE.md` — the recurring "I touched a shared table" trap).
- **A fresh environment needs manual Entra/DB steps.** See the README's **[⚠️ Manual one-time steps (cannot be
  automated here)](README.md#️-manual-one-time-steps-cannot-be-automated-here)** — the Postgres Entra admin +
  the MI's DB role must be created **once by an operator**; a deploy alone won't grant DB access.

## 4. How a change reaches prod

- **CI gates** (aggregated by the required `ci-gate`): `Test (xUnit + Postgres service)`,
  `Build (warnings as errors)`, `Claude review`, `Schema parity`, `Runner column parity`,
  `Trace-signals golden parity`, `Grant coverage` (×2), and the `mutation` gate.
- **Auto-deploys on merge to `main`** — CI publishes the Function App (OIDC; no stored Azure secret). There is
  no "deploy later" step.
- **★ Roll back:** _no rehearsed api rollback is documented_ — the implicit path is redeploying a prior commit's
  Function App. Treat it as **DRAFT · UNREHEARSED** (tracked in
  [OUTSTANDING.md](https://github.com/craigoley/synthwatch/blob/main/docs/handover/OUTSTANDING.md)). Rehearse
  before trusting it.

## 5. Where the gated truth lives

*If a doc and the code disagree, the code wins and the gate proves it.*

- **[`docs/auth-gates.md`](docs/auth-gates.md)** — the authoritative, gate-annotated endpoint table,
  **tripwire-enforced** by `AuthGatesDocParityTests` (fails the build if a `[Function]` route is missing or
  invented). Do **not** duplicate the route table anywhere.
- **The runner's `db/schema.sql`** — ground truth for the data model; the `Schema parity` + `Runner column
  parity` gates enforce this repo's fixture against it. (The old `docs/SCHEMA.md` was deleted for drifting.)
- **`grant-coverage`** — the MI/Postgres grants the API relies on, checked in CI.

## 6. Who to ask

Post-handover: **[Wegmans api owner — see the RACI](https://github.com/craigoley/synthwatch/blob/main/docs/handover/RACI.md)**,
**not Craig**. During the 30/60/90 shadow, Craig is on-call-for-questions only.
