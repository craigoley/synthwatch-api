# Auth/authz gates — the complete endpoint table

Handoff documentation: which gate protects every endpoint, by what mechanism, and whether it depends on
`AUTH_ENFORCEMENT_ENABLED`. Derived from the July 4 deep analysis; updated for the logout session-floor,
the fail-closed flag default, and the read-gate sweep. When adding an endpoint, follow the decision rule
in `CLAUDE.md` ("Auth gate decision rule").

## The model

Two layers:

1. **Middleware verb-gate** — `AuthorizationMiddleware` → pure `AuthGate.Decide` (exhaustively matrix-tested
   in `AuthGateTests`). GET/HEAD pass; mutating verbs require an editor/admin session, except the
   unauthenticated allowlist (`/auth/request-code|verify|request-access`) and the session-floor route
   (`/auth/logout` — any valid session). `/editors*` + `/access-requests*` mutations are admin-floor.
2. **Handler self-guards** — for reads the verb-gate can't see, the shared `SessionReadGate` (the #154
   pattern): 401 without a valid session, 403 when the live role isn't editor/admin. `EditorsFunctions`
   self-guards admin on every verb and **ignores the flag** (user management is never open).

`AUTH_ENFORCEMENT_ENABLED` is **fail-closed**: enforcement is ON unless the setting is explicitly
`false`/`0`; startup logs a warning while it is off. It exists for deploy safety only.

## Gate classes

- **anon** — open by design, no gate ever.
- **editor\*** — editor/admin session required *while enforcement is on* (inert only when the flag is
  explicitly off).
- **admin (always)** / **session (always)** — handler-enforced regardless of the flag.
- **field\*** — endpoint stays anon; a sensitive field serves only to an editor/admin session.

## Endpoint table

| Surface | Endpoints | Gate | Mechanism |
|---|---|---|---|
| Reports ×10 (`/reports/*`), `/sla`, `/status`, `/flows`, `/specs`, `/locations`, `/tags` (+`/suggested`), `/checks/{id}/runs`, `/checks/{id}/metrics`, `/checks/{id}/availability-series`, `/checks/{id}/locations` GET, `/checks/{id}/tags` GET, `/incidents` (+detail), `/channels/{id}/test/status`, `/notifications/health`, `/routing` GET, `/runs/{id}/steps`, `/health` | GET | **anon** | GET/HEAD open in the verb-gate; aggregate/status data is the public surface |
| `/checks` (list), `/checks/{id}` (detail) | GET | **anon + field\*** | endpoint open; `requestHeaders` serves only to a session (`SessionReadGate.HasWriteSessionAsync` — validation never scans request_headers for secrets, so the readback is gated instead) |
| `/channels` | GET | **editor\*** | `SessionReadGate` — `ChannelDto.config.authHeader` is a live webhook credential |
| `/reconcile/plan`, `/reconcile/drift` | GET | **editor\*** | `SessionReadGate` — plan carries the verbatim runner-emitted SQL apply executes; drift carries before/after config diffs. Responses are `Cache-Control: no-store` |
| `/runs/{id}/trace`, `/checks/{id}/success-trace`, `/runs/{id}/screenshot`, `/runs/{id}/trace-signals` | GET | **editor\*** (#154) | `SessionReadGate` (via `ArtifactsFunctions`) |
| checks/channels/routing/tags/locations CRUD; `/checks/{id}/run`; `/channels/{id}/test`; `/reconcile/trigger|approve|reject|apply`; `/runs/{id}/ai-insights`; `/runs/{runId}/baseline-diff`; `/checks/parse-intent` | POST/PUT/PATCH/DELETE | **editor\*** | middleware verb-gate (fail-closed by verb — new writes are gated automatically) |
| `/auth/request-code`, `/auth/verify`, `/auth/request-access` | POST | **anon (allowlisted)** | `AuthGate.UnauthWriteAllowlist` — you need these to obtain a session |
| `/auth/logout` | POST | **session (any role)\*** | `AuthGate.SessionFloorWriteRoutes` — a demoted editor can revoke their own session; no-token still 401 |
| `/auth/me` | GET | **session (always)** | handler check |
| `/editors` GET/POST/DELETE, `/access-requests` GET/DELETE | all | **admin (always)** | `EditorsFunctions.RequireAdminAsync` (flag-independent) + middleware admin-route for writes |

## Verifying the gates against a live deployment

```bash
BASE=https://synthwatch-api.azurewebsites.net/api

# anonymous → 401 problem+json on the gated reads
curl -si "$BASE/channels"        | head -1     # HTTP/1.1 401
curl -si "$BASE/reconcile/plan"  | head -1     # HTTP/1.1 401
curl -si "$BASE/reconcile/drift" | head -1     # HTTP/1.1 401

# with a session bearer → 200 (mint via the dashboard, or the admin session recipe in CLAUDE.md)
curl -si -H "Authorization: Bearer $TOKEN" "$BASE/channels"        | head -1   # HTTP/1.1 200
curl -si -H "Authorization: Bearer $TOKEN" "$BASE/reconcile/plan"  | head -1   # HTTP/1.1 200

# field gate: anonymous check detail has no requestHeaders; a session sees them
curl -s "$BASE/checks/1" | grep -c requestHeaders                              # 0
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/checks/1" | grep -c requestHeaders  # 1 (when configured)
```

## Operator config facts (env vars)

Three facts the API owns that the dashboard's #190 runbook flagged as *verify-from-API-side*. Each is
quoted at `file:line` against source (2026-07-05). The enforcement flag's **live** value is deliberately
left `needs-verification` — see below.

### Admin allowlist — `ADMIN_EMAILS`  ✅ confirmed

Admin is **env-based**, not a DB row, so an admin can't be locked out of their own allowlist edits. The
setting is comma-separated, normalized (lowercased/trimmed). Editors, by contrast, are the DB `editors` table.

- **Read (canonical):** `Infrastructure/AuthPrincipalService.cs:62` — `public static HashSet<string> AdminEmails() => (Environment.GetEnvironmentVariable("ADMIN_EMAILS") ?? string.Empty)…`; role resolution at `AuthPrincipalService.cs:53` (`if (AdminEmails().Contains(email))` → admin; else `editors` table → editor; else anonymous). Duplicated in `Functions/AuthFunctions.cs:220`.
- **Set:** `infra/main.bicep:216` (`name: 'ADMIN_EMAILS'` / `value: adminEmails`) from `param adminEmails string = ''` (`main.bicep:47`). Bicep note (`main.bicep:213`): *"ADMIN_EMAILS is the SECURITY source of truth for admin (the API enforces, not the dashboard)."*
- The dashboard's tooltip guess of `ADMIN_EMAILS` was **correct** — no rename needed.

### OTP delivery — email via Azure Communication Services (ACS)  ✅ confirmed

`POST /api/auth/request-code` issues a 6-digit code and delivers it by **email through Azure Communication
Services**, preferring **managed identity** (no ACS key stored on the Function App).

- **Path:** `Functions/AuthFunctions.cs:53` (`auth/request-code`) → `TrySendAsync(email, AuthEmailTemplates.SignInCode(code), ct)` (`AuthFunctions.cs:79`) → `IEmailSender.SendAsync` → `AcsEmailSender` (`Infrastructure/EmailSender.cs:25`).
- **Transport:** `Azure.Communication.Email.EmailClient` via `DefaultAzureCredential` against `ACS_EMAIL_ENDPOINT`, falling back to `ACS_EMAIL_CONNECTION_STRING`; sender address = `AUTH_EMAIL_FROM` (`EmailSender.cs:38`–`53`). RBAC: the MI holds **Communication and Email Service Owner** (role GUID `09976791-48a7-449e-bb21-39d1a415f350`) on the ACS resource — **auto-assigned by bicep** (`acsEmailOwnerAssignment`, `infra/main.bicep:356`–`360`), not a manual step.
- **Enumeration-safe:** a code is only *sent* to a known editor/admin; an unknown email gets a stored, unsendable code and the endpoint **always** returns 202 (`AuthFunctions.cs:48,76`).
- **Set:** `AUTH_EMAIL_FROM` (`infra/main.bicep:220`), `ACS_EMAIL_ENDPOINT` (`infra/main.bicep:224`).

### Enforcement flag — `AUTH_ENFORCEMENT_ENABLED`  ✅ name + wiring confirmed / ⚠️ live value needs-verification

- **Name + read:** `Infrastructure/AuthorizationMiddleware.cs:33` (`EnforcementEnabled()` → `Environment.GetEnvironmentVariable("AUTH_ENFORCEMENT_ENABLED")`); pure parse at `AuthorizationMiddleware.cs:37` — `!(raw == "false" || raw == "0")`, case-insensitive.
- **Semantics (code):** **fail-closed** — unset / empty / unrecognized → **ON**; only an explicit `false`/`0` turns it OFF (`AuthorizationMiddleware.cs:28`–`38`). `Program.cs:79`–`92` logs a loud startup warning whenever it resolves OFF.
- **Set:** `infra/main.bicep:229` (`name: 'AUTH_ENFORCEMENT_ENABLED'` / `value: string(authEnforcementEnabled)`) from `param authEnforcementEnabled bool = false` (`main.bicep:56`).
- ⚠️ **Live prod value = needs-verification.** Because bicep sets the var *explicitly*, the code's "unset → ON" fail-safe does **not** apply to a bicep deploy — the deployed value is whatever the param resolves to, and its **default is `false`** (→ `"False"` → OFF). No `.bicepparam` / parameters file / `deploy.yml` in this repo overrides it to `true`. So the live posture **cannot be derived from repo source alone** — read the deployed Function App's `AUTH_ENFORCEMENT_ENABLED` app setting to confirm whether prod is actually enforcing. (Do not assume ON from the code's fail-closed intent; the explicit bicep value wins.)
