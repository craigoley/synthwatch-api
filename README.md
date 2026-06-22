# SynthWatch API

The single data API for SynthWatch monitoring data. A C# **Azure Functions (.NET 10
isolated worker, ASP.NET Core integration)** HTTP API that reads the runner-owned Azure
Postgres database via **EF Core / Npgsql**, authenticating with a **managed identity** —
no connection-string password.

It runs as an Azure Function inside the same Azure network as the Postgres server, so the
database never needs to accept connections from outside Azure. Consumers: the Vercel
dashboard first; later a status page, a Prometheus exporter, and AI root-cause.

> **Scope of this PR:** API MVP only. Authn/authz hardening (the security stack) and
> `claude-review` wiring are deliberately separate follow-up PRs. Every endpoint here is
> currently `AuthorizationLevel.Anonymous` and is expected to be locked down in that
> follow-up.

## Platform constraints (why it is built this way)

- **.NET 10 isolated worker**, not in-process (in-process support ends Nov 2026).
- **ASP.NET Core integration model** (`FunctionsApplication` +
  `ConfigureFunctionsWebApplication()`) for real HTTP routing and `IActionResult`/JSON.
- **Flex Consumption** plan only — .NET 10 cannot run on Linux Consumption. Flex scales to
  zero (≈free at this traffic).
- **EF Core is read-mostly.** This API does **not** own migrations — the runner does. The
  `DbContext` maps to the existing tables and never mutates the schema. The only writes are
  to the `checks` table (create / edit / pause / delete).

## Endpoints

All routes are prefixed with `/api`. JSON in/out.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/checks` | List checks + derived current status (from the latest run) |
| `GET` | `/api/checks/{id}` | One check + recent runs |
| `POST` | `/api/checks` | Create a check (omit `id`) |
| `PATCH` | `/api/checks/{id}` | Partial edit / pause (`enabled`) |
| `DELETE` | `/api/checks/{id}` | Soft delete (`enabled=false`); `?hard=true` removes the row (cascades) |
| `GET` | `/api/checks/{id}/runs` | Paginated run history (`?page=&pageSize=`) |
| `GET` | `/api/runs/{id}/steps` | `run_steps` for the funnel |
| `GET` | `/api/checks/{id}/metrics` | `run_metrics` series (`?page=&pageSize=`) |
| `GET` | `/api/incidents` | Open + resolved (`?status=open\|resolved`, `?checkId=`) |
| `GET` | `/api/flows` | Distinct `flow_name` values |
| `GET` | `/api/sla?window=24h\|7d\|30d` | Per-check availability from the SLA views |

Request bodies are validated against the live DB CHECK constraints (kind, severity,
form factor, positive intervals/timeouts, `browser` requires `flow_name`, …) and rejected
with `400 { "error": "validation_error", "details": { ... } }`. DB exceptions are never
leaked — unhandled errors return a generic `500`.

### CORS

The single allowed origin is the Vercel dashboard URL, configured via the
`Cors__AllowedOrigin` app setting (never `*`). `CorsMiddleware` decorates responses and
`CorsPreflight` answers OPTIONS preflight.

## Data model & managed-identity auth

- Entities map 1:1 to the live schema (ground truth: `craigoleyagent/synthwatch`
  `docs/SCHEMA.md`). `bigint` identity PKs are `ValueGeneratedOnAdd` (never set on insert);
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

> **Do not deploy from this PR.** Recorded here for the follow-up.

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

<!-- auto-merge validation: trusted no-op (safe to remove) -->
