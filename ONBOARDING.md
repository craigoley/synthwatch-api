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

- **CI gates** (aggregated by the required `ci-gate`): `Test (xUnit + Testcontainers Postgres)`,
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
