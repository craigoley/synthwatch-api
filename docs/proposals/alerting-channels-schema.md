# Proposal: dashboard-managed alerting v1 — channels + routing schema (RUNNER-owned)

**Status:** DRAFT / coordination spec. **Owner of the actual migration: the runner** (`craigoley/synthwatch`,
runner-owns-schema). This document is the API side's request + recommended shape so the API CRUD endpoints
(`/api/channels`, `/api/routing`) can be built to **match the runner's migration exactly**. No schema is
created here; the API repo never owns migrations.

## Why this is blocked on the runner (recon, 2026-06-23)
- The contract's `channels` table and routing storage **do not exist** — no live table, no runner PR,
  no branch DDL, runner migrations stop at `0022`. There is no parallel PR to coordinate with.
- The existing live alerting model (**`0006_alert_profiles`**) is **different and conflicting**:
  - `alert_profiles {id, name, rules jsonb}`, `rules = [{severity, status, channels:["email","webhook"]}]`.
  - "channels" are **type-names** referencing **env-configured** transports — there is **no channel config
    in the DB** (recipients/URLs live in runner env today).
  - Per-check routing is `checks.alert_profile_id` → a profile (not a `channelIds` override map).
  - Routing is by **(severity × status)**; the new contract routes by **severity** (+ per-check) only.
- So the contract introduces a **new model** (DB channels carrying config, referenced by `channelIds`) that
  **supersedes** `alert_profiles`. That replacement + data migration is a runner + product decision.

## Contract (target shapes the API + dashboard build to)
- `channel`: `{ id, name, type: 'email'|'webhook', config, enabled }`
  - email `config`: `{ to: [...], from }` · webhook `config`: `{ url, authHeader? }`
- `routing`: severity defaults `{ [severity]: { channelIds: [...] } }` + per-check overrides
  `{ [checkId]: { channelIds: [...] } }`

## Recommended DDL (runner migration `0023`, additive / expand-contract)
```sql
BEGIN;

CREATE TABLE IF NOT EXISTS channels (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name       TEXT        NOT NULL UNIQUE,
    type       TEXT        NOT NULL,
    config     JSONB       NOT NULL DEFAULT '{}'::jsonb,
    enabled    BOOLEAN     NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT channels_type_chk CHECK (type IN ('email','webhook'))
);

-- Routing as NORMALIZED rows (not a jsonb blob) so referential integrity is a DB FK:
-- each row is EITHER a severity default (severity set) OR a per-check override (check_id set).
CREATE TABLE IF NOT EXISTS alert_routes (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    severity   TEXT,                                                  -- e.g. 'critical' | 'warning'
    check_id   BIGINT REFERENCES checks(id)   ON DELETE CASCADE,      -- per-check override
    channel_id BIGINT NOT NULL REFERENCES channels(id) ON DELETE RESTRICT,
    CONSTRAINT alert_routes_scope_chk CHECK (
        (severity IS NOT NULL AND check_id IS NULL) OR
        (severity IS NULL     AND check_id IS NOT NULL)
    ),
    CONSTRAINT alert_routes_uniq UNIQUE (severity, check_id, channel_id)
);

-- MI DML for the API write-path (if ALTER DEFAULT PRIVILEGES from the MI-grant setup doesn't
-- already cover new tables with full DML — confirm; #62/#63 showed SELECT/INSERT/DELETE auto-granted):
-- GRANT SELECT, INSERT, UPDATE, DELETE ON channels, alert_routes TO "synthwatch-api";

COMMIT;
```

### Why this shape
- **`ON DELETE RESTRICT` on `channel_id`** makes "delete a channel that a routing rule references" a DB-level
  block → the API returns **409** (the safer v1 behavior). No app-level scan needed.
- **`channel_id` / `check_id` FKs** enforce "referenced channelIds exist" and "per-check overrides vanish when
  the check is deleted" — referential integrity instead of jsonb bookkeeping.
- The API reconstructs the contract JSON (`{severityDefaults, checkOverrides}`) from `alert_routes` rows on
  GET, and diffs rows on PUT.

## Secret boundary (no transport secrets in the DB)
`channels.config` holds **targets only** — `to`/`from`/`url`/optional non-secret `authHeader`. The **transport
secret** (ACS connection string for email; any webhook signing secret) stays in **runner env** — the runner
reads the secret from env and the targets from `channels.config`. The API write-validation **rejects a config
containing a connection-string-like value** (it doesn't belong there).

## alert_profiles reconciliation (needs runner + product decision)
The new model replaces `0006_alert_profiles` + `checks.alert_profile_id`. Open decisions for the runner:
1. **Status dimension:** the old model routes by (severity × status); the contract routes by **severity** only.
   Confirm dropping the per-status routing (or fold `warn`/`resolved` handling elsewhere).
2. **Channel config seeding:** old channels are env type-names; the new model needs channel **rows** with config.
   Decide how the existing email/webhook transports become `channels` rows (operator-creates via the new API,
   or a seed from env).
3. **Deprecation:** after the runner reads `alert_routes`, drop `alert_profiles` + `checks.alert_profile_id`
   in a later contract migration (expand-contract).

## API endpoints this unblocks (built once the migration lands, to match it exactly)
- `GET/POST /api/channels`, `PUT/DELETE /api/channels/{id}` (DELETE → 409 when referenced, via the FK).
- `GET/PUT /api/routing` (reconstruct / diff `alert_routes`).
- Validation: per-type config shape (email: `to[]`+`from`; webhook: `url`), no-secret (reject connection
  strings), routing `channelIds` must exist **and be enabled**.

## Next step
Runner authors migration `0023` from this shape (adjusting as it sees fit — it owns the schema), confirms the
MI DML grant, and resolves the reconciliation decisions. The API CRUD PR then builds against the **landed**
shape (matching column names/types exactly), with tests against the updated Testcontainers snapshot.
