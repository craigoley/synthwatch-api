# SynthWatch API

The single data API for SynthWatch monitoring data. A C# **Azure Functions (.NET 10
isolated worker, ASP.NET Core integration)** HTTP API that reads the runner-owned Azure
Postgres database via **EF Core / Npgsql**, authenticating with a **managed identity** —
no connection-string password.

It runs as an Azure Function inside the same Azure network as the Postgres server, so the
database never needs to accept connections from outside Azure. Consumers: the Vercel
dashboard first; later a status page, a Prometheus exporter, and AI root-cause.

> **Status: production.** **Auth IS enforced** — the `AuthorizationMiddleware` verb-gate +
> handler self-guards require an editor/admin session for every write and for credential /
> forensic reads (fail-closed via `AUTH_ENFORCEMENT_ENABLED`; `/editors*` + `/access-requests*`
> are admin-only). The gate for **every** endpoint is in [`docs/auth-gates.md`](docs/auth-gates.md).
> This service **auto-deploys on merge to `main`** (CI publishes the Function App) — there is no
> "deploy later" step.

## Operations / On-call

Runbooks for a paged operator — reach for these first during a live incident:

- **[`docs/observability-tracing.md`](docs/observability-tracing.md)** — **500 in prod: find the
  exact exception by invocation id.** The `problem+json` `instance` field on any error IS the
  Function invocation id; one KQL query pivots it to the request + exception + stack.
  ⚠️ **Workspace-not-classic trap:** the telemetry lands in the **`App*` tables**
  (`AppRequests`, `AppExceptions`, …) of the **Log Analytics workspace** (`synthwatch-api-law`),
  **NOT** the classic Application Insights store — `az monitor app-insights query` returns
  **empty** and looks "dark" even while the exception is right there in the workspace. Query the
  workspace.
- **[`docs/auth-gates.md`](docs/auth-gates.md)** — **which endpoints are gated + how
  `AUTH_ENFORCEMENT_ENABLED` behaves** (fail-closed: enforcement is ON unless the setting is
  explicitly `false`/`0`). Also carries the operator config facts: the admin allowlist
  (`ADMIN_EMAILS`), the OTP email channel, and the enforcement-flag wiring.

## Platform constraints (why it is built this way)

- **.NET 10 isolated worker**, not in-process (in-process support ends Nov 2026).
- **ASP.NET Core integration model** (`FunctionsApplication` +
  `ConfigureFunctionsWebApplication()`) for real HTTP routing and `IActionResult`/JSON.
- **Flex Consumption** plan only — .NET 10 cannot run on Linux Consumption. Flex scales to
  zero (≈free at this traffic).
- **EF Core is read-mostly.** This API does **not** own migrations — the runner does. The
  `DbContext` maps to the existing tables and never mutates the schema. Its writes are scoped to
  the operator/config tables it owns — `checks`, `channels`, `alert_routes`/`tag_routes`,
  `editors`, `access_requests`, `sessions`, `error_mutes`, `env_domain_map`, and
  `reconcile_apply_plan` (approve/apply) — never the runner-owned run/incident data.

## Endpoints

All routes are prefixed with `/api`, JSON in/out. **The authoritative, gate-annotated route
list is [`docs/auth-gates.md`](docs/auth-gates.md)** — every endpoint, the gate that protects it,
and the mechanism. It is kept honest by a reflection test (`AuthGatesDocParityTests`) that fails
the build if any `[Function]` route is missing from it (or listed but deleted). This README does
**not** duplicate the route table on purpose: a hand-maintained copy with no tripwire is exactly
the thing that rots into a lie.

Request bodies are validated against the live DB CHECK constraints (kind, severity,
form factor, positive intervals/timeouts, `browser` requires `flow_name`, …) and rejected
with `400 { "error": "validation_error", "details": { ... } }`. DB exceptions are never
leaked — unhandled errors return a generic `500`.

### CORS

The single allowed origin is the Vercel dashboard URL, configured via the
`Cors__AllowedOrigin` app setting (never `*`). `CorsMiddleware` decorates responses and
`CorsPreflight` answers OPTIONS preflight.

## Data model & managed-identity auth

- Entities map 1:1 to the live schema. **Ground truth is the runner's `db/schema.sql`**
  (`craigoley/synthwatch`) — the schema-parity CI gate already enforces it as source of truth
  against the test fixture, so it cannot silently drift. (The old `docs/SCHEMA.md` hand-copy
  was **deleted** in the runner — it had drifted ~52 migrations; use `db/schema.sql`.) `bigint` identity PKs are
  `ValueGeneratedOnAdd` (never set on insert);
  `transfer_bytes` / `js_heap_bytes` / `perf_budget_transfer_bytes` map to `long`.
- The SLA data comes from the existing `sla_availability(p_from, p_to)` function and the
  `sla_availability_24h/7d/30d` views, read through a keyless entity — not reimplemented.
- **No password anywhere.** `PostgresDataSourceFactory` builds an `NpgsqlDataSource` whose
  password is an Entra access token for scope
  `https://ossrdbms-aad.database.windows.net/.default`, acquired via
  `DefaultAzureCredential` and refreshed by Npgsql's periodic password provider. The
  connection string carries only host / database / username (= the MI principal name).
  **If a password were present, the managed-identity token would be ignored.**

### Structured SSL cert days-remaining

The dashboard / status page / alert profiles need cert **days-remaining** as a typed field so
they don't regex-parse prose. Two structured cert columns are surfaced:

- `checks.cert_expiry_warn_days` (`int NOT NULL DEFAULT 30`) — the per-check warn *threshold*
  (config input). Exposed as `certExpiryWarnDays` on the check DTOs and accepted on write.
- `runs.cert_days_remaining` (`int NULL`, populated on ssl runs) — the measured value. Exposed
  additively as `certDaysRemaining` (`number | null`; null for non-ssl runs) on `RunDto`, and as
  `lastCertDaysRemaining` (latest run's value) on the check summary.

We deliberately do **not** parse `error_message` in the API (fragile; breaks on wording changes) —
it stays the human-readable text alongside the structured field.

## Local development

```bash
# Restore + build
dotnet build

# Configure local.settings.json (gitignored) — Postgres__* + Cors__AllowedOrigin.
# DefaultAzureCredential will use your `az login` identity locally; that identity must have
# a Postgres role created the same way as the MI (see manual steps) to actually read data.
func start
```

`local.settings.json` ships with placeholder values and is git-ignored.

## Deploy

> **This service auto-deploys on merge to `main`** (CI publishes the Function App). The commands
> below are the underlying one-time provisioning / publish steps CI runs — an operator rarely runs
> them by hand.

```bash
# 1. Provision infra into the EXISTING resource group (creates storage, Flex plan,
#    App Insights, and the Function App with a system-assigned identity).
az deployment group create -g synthwatch-rg -f infra/main.bicep \
  -p pgHost=<server>.postgres.database.azure.com \
     allowedCorsOrigin=https://<dashboard>.vercel.app

# 2. Publish the code to the Function App.
func azure functionapp publish synthwatch-api
```

The Bicep targets **Flex Consumption** (`FC1` / `FlexConsumption`) in `synthwatch-rg` /
`eastus2`, gives the app a **system-assigned managed identity**, sets the `Postgres__*` and
`Cors__AllowedOrigin` app settings, and uses identity-based storage (no account keys).

Outputs: `functionAppUrl`, `apiBaseUrl`, `functionAppPrincipalId`, `functionAppNameOut`.

## ⚠️ Manual one-time steps (cannot be automated here)

These must be run **once by an operator** — they require Postgres / Entra admin rights that
the deployment identity does not have.

### 1. Enable Microsoft Entra authentication on the Postgres Flexible Server

Portal: **Postgres Flexible Server → Authentication → enable Microsoft Entra
authentication** (Entra-only or PostgreSQL-and-Entra), set yourself as an **Entra admin**,
and save. Or via CLI:

```bash
az postgres flexible-server update -g synthwatch-rg -n <server> \
  --active-directory-auth Enabled

az postgres flexible-server ad-admin create -g synthwatch-rg -s <server> \
  --display-name <your-entra-upn> --object-id <your-entra-object-id> --type User
```

### 2. Create the DB role for the Function App's managed identity

Connect to the **`postgres`** database **as the Entra admin** (use your own Entra token as
the password):

```bash
az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv  # use as password
psql "host=<server>.postgres.database.azure.com port=5432 dbname=postgres \
      user=<your-entra-upn> sslmode=require"
```

Then create the principal. For a **system-assigned managed identity** the role name is the
Function App name (`synthwatch-api`); use the `principalId` from the Bicep output:

```sql
-- Preferred for managed identities: bind by object id, type 'service'.
SELECT * FROM pgaadauth_create_principal_with_oid(
  'synthwatch-api',                       -- role name = Function App name = Postgres__Username
  '<functionAppPrincipalId-from-bicep>',  -- the MI object/principal id
  'service',                              -- managed identity / service principal
  false,                                  -- isAdmin
  false);                                 -- isMfa

-- (Alternative if name resolution is configured: SELECT * FROM pgaadauth_create_principal('synthwatch-api', false, false);)
```

### 3. Grant the role its read-mostly + checks-CRUD privileges

Connect to the **application database** (`synthwatch`) as **`synthadmin`** — the migration role
that owns every public table. Ownership is required for `GRANT ... ON ALL TABLES` and for
`ALTER DEFAULT PRIVILEGES FOR ROLE synthadmin`; the Entra admin can only run these if it is a
member of `synthadmin`. Then grant:

```sql
-- READ-ALL (strictly read-only). The API is read-mostly and the sla_availability() SQL function
-- runs with the INVOKER's (the MI's) privileges, so the MI needs SELECT on every table the
-- function joins. Rather than chase each new runner table (e.g. maintenance_windows, added by
-- migration 0004 — a missing SELECT there 500'd every /api/sla window), grant read on all current
-- tables/views + execute on all functions, and set DEFAULT PRIVILEGES so future runner tables are
-- auto-readable. This closes that whole "permission denied" class without any write escalation.
GRANT SELECT  ON ALL TABLES    IN SCHEMA public TO "synthwatch-api";
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO "synthwatch-api";

-- Future-proof: objects the runner creates later are auto-granted. DEFAULT PRIVILEGES apply to the
-- role that CREATES the object, so they MUST target the migration/owning role (here `synthadmin`,
-- which owns every public table). Run these AS synthadmin, or as a member of it via FOR ROLE:
ALTER DEFAULT PRIVILEGES FOR ROLE synthadmin IN SCHEMA public GRANT SELECT  ON TABLES    TO "synthwatch-api";
ALTER DEFAULT PRIVILEGES FOR ROLE synthadmin IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO "synthwatch-api";

-- Checks CRUD: create / edit / pause (UPDATE enabled) / hard delete (cascades). This is the ONLY
-- write the MI gets — no write on runs/run_steps/run_metrics/incidents, no superuser.
GRANT INSERT, UPDATE, DELETE ON checks TO "synthwatch-api";
-- 'id' is GENERATED ALWAYS AS IDENTITY, so no sequence grant is needed for inserts.
```

After these steps the Function App can read all monitoring data (now and for future runner
tables), execute the SLA function, and manage `checks` — with no secret of any kind and no write
access beyond `checks`.

<!-- claude-review end-to-end validation: trivial docs touch, safe to merge. -->
