# SynthWatch API — Deep Review 2026-07 (docs only)

**Date:** 2026-07-02
**Repo:** craigoley/synthwatch-api @ `ab87603` (main at time of analysis)
**Scope:** overnight deep analysis — system map, mapper-drift systemic analysis, consumer-side grants, runtime correctness, code health, boundary contracts, tech-debt register.
**Rails respected:** local build/test/static analysis only. No `az` commands, no remote Postgres/AOAI/App Insights connections, no fixes applied. Diff is `docs/analysis/**` only.

---

## Methodology & evidence contract

- Every finding cites `file:line` (at commit `ab87603`) or captured command output.
- **OBSERVED** = read in source or produced by a command run here. **INFERRED** = deduced; labeled as such.
- A falsification check was run before every Critical/Major finding (documented inline).
- External behavior (Azure Functions runtime, Npgsql token/pool semantics, Postgres privilege rules) verified against official docs/issues, not memory. Note: this sandbox's egress proxy blocks direct fetches of `npgsql.org` / `learn.microsoft.com` / `postgresql.org`; verification used search-indexed excerpts of those official pages plus directly-fetched GitHub issues (`github.com` reachable). Each such claim cites its source URL.
- Verification environment: .NET SDK 10.0.109 (Ubuntu 24.04), Docker + Testcontainers (postgres:16 via mirror.gcr.io), `dotnet-coverage` 18.x.
- **Test evidence:** full suite green locally — `Passed! - Failed: 0, Passed: 328, Skipped: 1, Total: 329` (the one skip is `TraceSignalsGoldenParityTests.FromZip_golden_input_matches_expected_json`, by design: it needs the runner repo checkout and runs in the dedicated cross-repo trace-parity CI job — `tests/SynthWatch.Api.Tests/TraceSignalsGoldenParityTests.cs:18,39-43`).

## RECON: diff against prior analysis (`FINDINGS-REPORT.md`, 2026-06-28)

This report does **not** rediscover the 2026-06-28 findings; it re-checked their status at `ab87603` (12 feature PRs have landed since, #139–#150):

| Prior finding | Status at ab87603 |
|---|---|
| A1 — baseline-diff transient blob error → 404 | **NARROWED, not fixed.** `ResolveSignalsAsync` now falls back to persisted `trace_signals` JSON when the zip read isn't `Ok` (`Functions/LocationDiffFunctions.cs:127-149`), so a transient blob error is masked whenever persisted signals exist. When they don't (and for `Unavailable` on the baseline-less failing path), `null` → 404 at `LocationDiffFunctions.cs:64-66` still collapses transient into permanent. |
| A2 — `rca.generated_at` snake_case leak | **UNCHANGED** (`Data/Entities/IncidentRca.cs:25`). Still the documented jsonb dual-use trap. |
| B1 — no AOAI rate limiting | **UNCHANGED**, and surface grew: `POST /checks/parse-intent` (#132) is a third AOAI-spending endpoint (`Functions/ParseIntentFunctions.cs`). |
| B4 — `AUTH_ENFORCEMENT_ENABLED` defaults OFF | **UNCHANGED** (`Infrastructure/AuthorizationMiddleware.cs:28-32`). |
| B5 — logout not on unauth allowlist | **UNCHANGED** (`Infrastructure/AuthGate.cs:28-29` — allowlist is request-code/verify/request-access only). |
| D1 — auth logic duplicated in AuthFunctions | **UNCHANGED** (`Functions/AuthFunctions.cs` still has private `ResolveRoleAsync`). |
| D3 — health endpoint anonymous object | **UNCHANGED** (`Functions/HealthFunctions.cs:35,41-49`). |
| D4 — NotificationsFunctions raw `OkObjectResult` | **UNCHANGED** (`Functions/NotificationsFunctions.cs:49`). |

New ground covered here that the prior report did not: mapper-drift systemic inventory (§2), consumer-side grants extraction + CI-gate gap analysis (§3), Npgsql/MI pool audit, cancellation-token and cold-start audit (§4), analyzer-delta/coverage/package scans (§5), and the full EXPOSES/CONSUMES contract (§1/§6).

---

## 1. SYSTEM MAP AS-OBSERVED

**56 HTTP endpoints** across 23 `Functions/*.cs` files. Route prefix `api` from `host.json` (`extensions.http.routePrefix`, `host.json:15-19`). Every endpoint declares `AuthorizationLevel.Anonymous`; real auth is the middleware gate below.

### 1.1 Effective auth model

- **Verb-based, fail-closed gate** (`Infrastructure/AuthGate.cs`, `Infrastructure/AuthorizationMiddleware.cs`): GET/HEAD always open (`AuthGate.cs:71-72`); POST/PUT/PATCH/DELETE require a valid editor/admin bearer session (`AuthGate.cs:73-83`) — **only when `AUTH_ENFORCEMENT_ENABLED` is `true`/`1`; default OFF = every write open** (`AuthorizationMiddleware.cs:28-32`).
- Unauthenticated-write allowlist: `/auth/request-code`, `/auth/verify`, `/auth/request-access` (`AuthGate.cs:28-29`).
- Admin-only mutating route groups: `/editors*`, `/access-requests*` (`AuthGate.cs:54-61`).
- Principal: opaque bearer → sha256 → live `sessions` row → role re-resolved per request from `ADMIN_EMAILS` / `editors` table (`Infrastructure/AuthPrincipalService.cs:34-58`); role never trusted from the client.
- `EditorsFunctions` additionally self-guards admin on **every** verb including GETs, independent of the flag (`Functions/EditorsFunctions.cs:43-57`) — the only handler-level auth check.
- Denials audited best-effort, returned as problem+json 401/403 (`AuthorizationMiddleware.cs:89-98`).

### 1.2 Endpoint inventory

Auth column: "editor write\*" = editor/admin required only when enforcement is on (default off → open). All routes below are under `/api`.

| # | Function | Verb | Route | Auth | Response type (wire shape) |
|---|---|---|---|---|---|
| 1 | ListChecks | GET | /checks | open | bare `CheckSummaryDto[]`; Cache-Control 10s (`ChecksFunctions.cs:122-124`) |
| 2 | GetCheck | GET | /checks/{id:long} | open | `CheckDetailDto` (incl. `slo`); 404 |
| 3 | CreateCheck | POST | /checks | editor write\* | `CheckDetailDto` (201); 400 (JSON/validation/B10), 409 dup source_key |
| 4 | UpdateCheck | PATCH | /checks/{id:long} | editor write\* | `CheckDetailDto`; 400, 404 |
| 5 | DeleteCheck | DELETE | /checks/{id:long} | editor write\* | 204; `?hard=true` hard-deletes; 404 |
| 6 | ListCheckRuns | GET | /checks/{id:long}/runs | open | `RunsPage {items,nextCursor,pageSize,latestRunId}`; keyset cursor; 400, 404 |
| 7 | ListCheckMetrics | GET | /checks/{id:long}/metrics | open | `PagedResult<RunMetricDto> {items,page,pageSize,total}`; 404 |
| 8 | GetAvailabilitySeries | GET | /checks/{id:long}/availability-series | open | `AvailabilitySeriesDto {window,bucket,points}`; 400, 404; 30s cache |
| 9 | AuthRequestCode | POST | /auth/request-code | **unauth allowlist** | `MessageDto` (202, uniform, enumeration-safe; silent rate-limit 5/15min) |
| 10 | AuthVerify | POST | /auth/verify | **unauth allowlist** | `VerifyResponseDto {token,email,role,expiresAt}`; uniform 400; attempt cap 5 |
| 11 | AuthMe | GET | /auth/me | open (voluntary token) | `MeDto {email,role}`; 401 problem+json when no session |
| 12 | AuthLogout | POST | /auth/logout | editor write\* (NOT allowlisted) | `MessageDto` (200), idempotent |
| 13 | AuthRequestAccess | POST | /auth/request-access | **unauth allowlist** | `MessageDto` (200, uniform; rate-limit 3/24h) |
| 14 | GetChannels | GET | /channels | open | bare `ChannelDto[]` |
| 15 | CreateChannel | POST | /channels | editor write\* | `ChannelDto` (201); 400 (incl. dup name — see §1.5) |
| 16 | UpdateChannel | PUT | /channels/{id:long} | editor write\* | `ChannelDto`; 400, 404 |
| 17 | DeleteChannel | DELETE | /channels/{id:long} | editor write\* | 204; 404; **409 legacy anonymous body (§1.5)** |
| 18 | TestChannel | POST | /channels/{id:long}/test | editor write\* | `ChannelTestAcceptedDto {requestId}` (202); 404 |
| 19 | TestChannelStatus | GET | /channels/{id:long}/test/status | open | `ChannelTestStatusDto {status,detail,requestedAt,completedAt}`; 400, 404 |
| 20 | NotificationsReadiness | GET | /notifications/health | open | `NotificationsReadinessDto`; raw `OkObjectResult` |
| 21 | GetRouting | GET | /routing | open | `RoutingDto {severity,perCheck,tagRules}` (each null when empty) |
| 22 | SetRouting | PUT | /routing | editor write\* | `RoutingDto`; many 400s (anti-wipe, referential checks — `RoutingFunctions.cs:68-146`) |
| 23 | ListEditors | GET | /editors | **admin (handler, always on)** | bare `EditorDto[]`; 401/403 |
| 24 | AddEditor | POST | /editors | **admin (gate+handler)** | `EditorDto` (201); 400, 409 |
| 25 | RemoveEditor | DELETE | /editors/{email} | **admin (gate+handler)** | 204; 404 |
| 26 | ListAccessRequests | GET | /access-requests | **admin (handler, always on)** | bare `AccessRequestDto[]` |
| 27 | DismissAccessRequest | DELETE | /access-requests/{email} | **admin (gate+handler)** | 204 idempotent (`ExecuteDeleteAsync`) |
| 28 | ListRunSteps | GET | /runs/{id:long}/steps | open | bare `RunStepDto[]`; 404 |
| 29 | GetRunTrace | GET | /runs/{id:long}/trace | open | `FileStreamResult` zip (attachment); 404, 503 |
| 30 | GetCheckSuccessTrace | GET | /checks/{id:long}/success-trace | open | `FileStreamResult` zip; 404, 503 |
| 31 | GetRunScreenshot | GET | /runs/{id:long}/screenshot | open | `FileStreamResult` png (inline); 404, 503 |
| 32 | GetTraceSignals | GET | /runs/{id:long}/trace-signals | open | `TraceSignalsDto {targetHost,network,console}`; 404, 503; bad zip → 200 empty |
| 33 | GetAiInsights | POST | /runs/{id:long}/ai-insights | editor write\* | `AiInsightsDto`; 404; AOAI failures → 200 with honest `note`/`retryable`, never 500 |
| 34 | GetBaselineDiff | POST | /runs/{runId:long}/baseline-diff | editor write\* | `LocationDiffDto`; 404, 503; B10 redaction for sensitive checks |
| 35 | ListIncidents | GET | /incidents | open | `CursorPage<IncidentDto> {items,nextCursor,pageSize}`; 400; `status=open` exempt from window |
| 36 | GetIncident | GET | /incidents/{id:long} | open | `IncidentDetailDto` (timeline, perLocation, recurrence); 404; 10s cache |
| 37 | GetSla | GET | /sla | open | `SlaResponseDto {window,fleet,items}`; 400; view-name allowlist |
| 38 | GetStatus | GET | /status | open | `StatusPageDto {window,properties,recentIncidents}`; 15s cache |
| 39 | ListFlows | GET | /flows | open | bare `FlowDto[]`; 30s cache |
| 40 | GetSpecCatalog | GET | /specs | open | `SpecCatalogDto {items,probedAt}`; 30s cache |
| 41 | Health | GET | /health | open | anonymous `{status,db}` / 503 `{status,db,detail}` (§1.5) |
| 42 | GetLocations | GET | /locations | open | `LocationsResponse {locations:[{name,enabled}]}` |
| 43 | GetCheckLocations | GET | /checks/{id:long}/locations | open | `CheckLocationsResponse {locations:[string]}`; 404 |
| 44 | SetCheckLocations | PUT | /checks/{id:long}/locations | editor write\* | `CheckLocationsResponse`; 400 (B10/empty/unknown), 404 |
| 45 | GetCheckTags | GET | /checks/{id:long}/tags | open | `CheckTagsResponse {tags:[{key,value}]}`; 404 |
| 46 | SetCheckTags | PUT | /checks/{id:long}/tags | editor write\* | `CheckTagsResponse`; 400 (anti-wipe), 404 |
| 47 | GetTagsInUse | GET | /tags | open | `TagsInUseResponse {tags:[{key,value,count}]}` |
| 48 | GetSuggestedTagKeys | GET | /tags/suggested | open | **bare `string[]`** (static, `Infrastructure/TagNormalization.cs:15`) |
| 49 | RunCheckNow | POST | /checks/{id:long}/run | editor write\* | `RunNowAcceptedDto {requestId}` (202); 404; 409 paused; idempotent coalesce |
| 50 | ParseMonitorIntent | POST | /checks/parse-intent | editor write\* | `ParseIntentDto` (validate-don't-trust prefill); 400; never persists |
| 51 | TriggerReconcile | POST | /reconcile/trigger | editor write\* | `ReconcileTriggeredDto {triggered}` (202); 503 on failed ARM start |
| 52 | GetReconcileDrift | GET | /reconcile/drift | open | `ReconcileDriftDto {items,detectedAt}` |
| 53 | GetReconcilePlan | GET | /reconcile/plan | open | `ReconcileApplyPlanDto {items,computedAt}` |
| 54 | Approve/RejectReconcilePlan | POST | /reconcile/approve, /reconcile/reject | editor write\* | anonymous `{sourceKey,driftType,status}` (§1.5); 400, 404, 409×3 |
| 55 | ApplyReconcilePlans | POST | /reconcile/apply | editor write\* | `ReconcileApplyResultDto {applied,failed,cap}`; cap 5; per-plan txn + shape-guards (`ReconcileFunctions.cs:280-321`) |
| 56 | Reports ×10 | GET | /reports/{deploys,egress,slo,mttr,incident-breakdown,trust,trust/{checkId},availability,performance,narrative} | open | typed report DTOs (see §6); shared `?window` allowlist 7d\|30d\|90d → 400 (`ReportsFunctions.cs:27-33`); `?tag=key:value` AND-filter (`ReportsFunctions.cs:116-121`); narrative 404 when absent; deploys tolerates missing table (42P01 → empty, `ReportsFunctions.cs:60-63`) |

Validation helpers: `Infrastructure/CheckValidation.cs` (create/patch + B10 `SensitiveNeedsRedaction`, mirrors runner `reconcile.ts:181` — `CheckValidation.cs:434`), `Infrastructure/AlertingValidation.cs:14-47` (channel type/config + transport-secret rejection), `Infrastructure/CursorPaging.cs:28-129` (cursor/window parsing → specific 400s), `Infrastructure/Paging.cs:11-22` (offset paging — invalid values silently fall back to defaults, never 400).

### 1.3 DI registrations (Program.cs) — lifetimes

| Registration | Lifetime | Purpose | Cite |
|---|---|---|---|
| App Insights worker service + Functions AI | framework | telemetry | Program.cs:15-17 |
| `PostgresOptions` ("Postgres" section) | options singleton | DB settings | Program.cs:20 |
| `NpgsqlDataSource` via `PostgresDataSourceFactory.Create` | **singleton** | one MI-authed data source (pool) | Program.cs:26 |
| `Azure.Core.TokenCredential` → `DefaultAzureCredential` | **singleton** | MI credential for blob/ARM/AOAI | Program.cs:30 |
| `IArtifactReader` → `ArtifactReader` | **singleton** | the one blob fetcher (host allowlist + status classification) | Program.cs:34 |
| `IEmailSender` → `AcsEmailSender` | **singleton** | OTP/access emails via ACS | Program.cs:39 |
| `AddHttpClient()` | factory | `IHttpClientFactory` for ARM | Program.cs:45 |
| `RunnerJobOptions` | options singleton | ACA job coordinates | Program.cs:46 |
| `IRunnerJobTrigger` → `ArmRunnerJobTrigger` | **singleton** | on-demand ACA job start via ARM | Program.cs:47 |
| `IAoaiClient` → `AoaiClient` | typed HttpClient (transient, factory-managed) | AOAI chat completions; inert when unconfigured | Program.cs:52 |
| `SynthWatchDbContext` | **scoped** | EF Core over runner-owned schema (no migrations) | Program.cs:55-59 |
| `IAuthPrincipal` → `AuthPrincipalService` | **scoped** | bearer → session → live role | Program.cs:63 |
| `IAuditScope` → `AuditScope` | **scoped** | per-request audit-diff channel | Program.cs:64 |

Note the DB credential split (OBSERVED): the **DB** token uses a `DefaultAzureCredential` constructed privately inside `PostgresDataSourceFactory.Create` (`Infrastructure/PostgresDataSourceFactory.cs:45`), while blob/ARM/AOAI share the DI singleton from `Program.cs:30` — two credential instances, two independent token caches.

**Middleware pipeline** (outermost→innermost, `Program.cs:69-71`): `RequestLoggingMiddleware` (sets `RequestCorrelation.Current` = InvocationId, `RequestLoggingMiddleware.cs:32-33`) → `ExceptionHandlingMiddleware` (shields all, emits 500 problem+json) → `AuthorizationMiddleware` (gate inside shielding: session-lookup error → shielded 500 = fail-closed denial).

### 1.4 Error contract

- Content type `application/problem+json` (`Infrastructure/ProblemResults.cs:12`). Body (RFC 9457 + legacy extensions, `ProblemResults.cs:16-29`): `{type:"about:blank", title, status, detail, instance, error, message}` — `instance` = correlation/invocation id, `error` = machine code (`not_found|bad_request|conflict|unauthorized|forbidden|unavailable|internal_error|validation_error`), `message` mirrors `detail`.
- Validation 400 adds `details: {field: error}` (`ProblemResults.cs:36-46`).
- Emitters: handlers via `ApiResults.*` (`Infrastructure/ApiResults.cs:16-51`); 500s via `ExceptionHandlingMiddleware.cs:47-50`; gate 401/403 via `AuthorizationMiddleware.cs:89-98`.

### 1.5 Deviations (all OBSERVED, falsified against source)

1. **`ApiResults.Created` silently drops its `location` argument** — `ApiResults.cs:40-41`: the parameter is unused; 201s from CreateCheck/CreateChannel/AddEditor carry **no `Location` header** even though call sites pass one.
2. **`Health`** returns anonymous `{status,db}` / 503 `{status,db,detail}`, not problem+json (`Functions/HealthFunctions.cs:35,41-49`).
3. **`DeleteChannel` 409** returns a legacy anonymous `{error,message}` body, plain `application/json`, missing `type/title/status/detail/instance` (`Functions/ChannelsFunctions.cs:127-131`).
4. **Reconcile approve/reject 200** returns an anonymous `{sourceKey,driftType,status}` object, no DTO (`Functions/ReconcileFunctions.cs:165`).
5. **Duplicate-name status inconsistency:** channel duplicate → **400** (`ChannelsFunctions.cs:68-71`) vs check duplicate source_key → **409** (`ChecksFunctions.cs:230-235`).
6. `NotificationsFunctions` uses raw `OkObjectResult` (cosmetic; `NotificationsFunctions.cs:49`).
