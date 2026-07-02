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

**66 HTTP endpoints** across 23 `Functions/*.cs` files (OBSERVED: `grep -c "[Function(" Functions/*.cs` = 66; the table below has 56 rows because the 10 report GETs and approve/reject share rows). Route prefix `api` from `host.json` (`extensions.http.routePrefix`, `host.json:15-19`). Every endpoint declares `AuthorizationLevel.Anonymous`; real auth is the middleware gate below.

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

---

## 2. MAPPER DRIFT — SYSTEMIC ANALYSIS

This repo's confirmed production bug class: hand-written shape mappings silently drifting from what a consumer expects (historical instances: the flat-vs-nested `getPerformanceReport` mismatch; the `{date,value}` vs `{day,availabilityPct}`/`{day,avgMs}` series cluster; the trace-signals `mutations` drift fixed by runner #169/api #148; the reconcile-plan positional `spec_path` drift fixed in #131). Local git history is truncated to 50 commits (oldest #101, OBSERVED `git log --oneline | wc -l` = 50), so the first two instances predate retained history — their residue is documented in code comments (`ReportsFunctions.cs:659-663` records the `groupBy="none"` empty-report bug; `ChecksFunctions.cs:434-457` the DST bucket-drop; `Data/Entities/ReportNarrativeRow.cs:9-10` a "built ahead of the runner migration" contract).

### 2a. Mapping/projection inventory

**Mapping convention (applies to every raw-SQL row):** keyless row types are registered `HasNoKey().ToView(null)` with explicit `HasColumnName("snake_case")` per property in `Data/SynthWatchDbContext.cs` (SlaAvailabilityRow :368-381, TrustMonitorRow :448-471, SpecCatalogRow :654-677, …). Binding is **by column name, not position**; there are no `[Column]` attributes. Every raw-SQL read is therefore a hand-written 3-link chain: SQL `AS` alias (Functions) → `HasColumnName` (DbContext) → C# property → (usually) a hand-written DTO projection. Nothing type-checks link 1→2.

Totals (OBSERVED, exhaustive sweep): **23 raw-SQL → keyless-row boundaries**, **~20 row/entity → DTO projections** (6 extracted to pure `Infrastructure/*Projection.cs` helpers, the rest inline), **11 typed jsonb contracts + 6 jsonb-as-text passthroughs**, plus 4 write-side SQL mirrors of runner TS logic. All hand-written; zero generated mappers. IT = `tests/SynthWatch.Api.Tests/IntegrationTests.cs` (executes the real handler classes against Testcontainers Postgres).

| # | Mapping boundary | Kind | Tested by | Risk notes |
|---|---|---|---|---|
| 1 | `MetricsSql` 7 aliases → `CheckMetricsRow` (ChecksFunctions.cs:23-65→:97; DbContext :690-701) | raw-SQL | IT:114 | `spark` is `json_agg(...)::text` → #2 |
| 2 | spark JSON keys `t,d,s` → `SparkPoint` `[JsonPropertyName]` (ChecksFunctions.cs:58→:100; Dtos/CheckDtos.cs:12-15) | jsonb-text | IT:126 (`NotEmpty` only — keys not pinned) | 3-hop: SQL alias → JSON key → dashboard shape |
| 3 | `CheckMetricsRow` → `CheckMetricsDto` (ChecksFunctions.cs:98-100) | hand map | IT:114; MappingTests.cs:54 | |
| 4 | `SELECT * FROM slo_status(...)` → `SloStatusRow` (ChecksFunctions.cs:178-182; DbContext :384-399) | raw-SQL | IT:134, IT:615 | **`SELECT *`** — a runner rename of the fn's RETURNS TABLE silently breaks binding |
| 5 | `SloStatusRow` → `SloDto` + burn thresholds (ChecksFunctions.cs:154-176) | hand map | IT:134 | 14.4x/6x thresholds duplicated from runner alerting |
| 6 | availability-series bucketed SQL → `AvailabilitySeriesPointRow` (ChecksFunctions.cs:458-482; DbContext :542-550) | raw-SQL | IT:693/746/759 (incl. DST) | the endpoint family of the past series drift; SLA taxonomy mirrored by hand (comment :453-457) |
| 7 | row → `AvailabilityPointDto` (ChecksFunctions.cs:484-486) | hand map | IT:693 | |
| 8 | `SELECT *` from `sla_availability_{24h,7d,30d,90d}` views → `SlaAvailabilityRow` (SlaFunctions.cs:30-42) | raw-SQL | IT:647, IT:682 | runner owns the views; `SELECT *` again |
| 9 | row → `SlaDto`/`SlaFleetDto` (`Infrastructure/SlaProjection.cs:23-60`) | pure helper | SlaProjectionTests + IT:647 | |
| 10-11 | deploys SQL → `DeployRow` → `DeployMarkerDto` (ReportsFunctions.cs:54-68; DbContext :517-527) | raw-SQL + map | IT:3401 | 42P01-tolerant (merged≠migrated guard) |
| 12-13 | egress SQL → `EgressRunRow` → `EgressReportDto` (ReportsFunctions.cs:95-102; `EgressReportProjection.cs:15-33`) | raw-SQL + pure helper | IT:3455 | |
| 14-15 | SLO-report LATERAL `slo_status`+`slo_burn_status` → `SloReportRow` → DTOs (ReportsFunctions.cs:148-162; `SloReportProjection.cs:20-67`) | raw-SQL + pure helper | IT:182 | aliases must track two runner fns |
| 16-17 | MTTR SQL (`rca->>'classification'`) → `MttrIncidentRow` → DTOs (ReportsFunctions.cs:192-204; `MttrReportProjection.cs:26-100`) | raw-SQL + pure helper | IT:275 | runner reconcile.ts taxonomy duplicated as SQL literals |
| 18-19 | incident-breakdown SQL → row → DTO inline (ReportsFunctions.cs:233-260) | raw-SQL + inline | IT:3238, IT:3288 | |
| 20-21 | `TrustFleetSql`/`TrustDetailSql` → `TrustMonitorRow` → `TrustMonitorDto` (ReportsFunctions.cs:339-444; `TrustReportProjection.cs:80-107`) | raw-SQL + pure helper | IT:413, IT:565 | **20-alias SELECT duplicated verbatim in two methods** — fleet/detail can drift from each other |
| 22 | trust retry-day SQL → `TrustRetryDayRow` → `TrustRetryPointDto` (ReportsFunctions.cs:309-327) | raw-SQL + map | IT:538 | |
| 23-27 | availability/latency/vitals/series SQL (each ×2 grouped/ungrouped) → 5 row types (ReportsFunctions.cs:462-638; DbContext :552-612) | raw-SQL | IT:1033, IT:1146, IT:1205, IT:1268 | grouped/ungrouped SQL duplicated; `CheckId NULL` = group row convention |
| 28 | rows → `AvailabilityReportDto`/`PerformanceReportDto` nested assembly (ReportsFunctions.cs:505-516, :640-675) | hand map inline | IT:1033/1146 + envelope pins | **the getPerformanceReport flat-vs-nested drift lived here** |
| 29 | narrative SQL (`highlights::text`, `fact_pack::text`) → `ReportNarrativeRow` → `NarrativeDto` (ReportsFunctions.cs:702-729; DbContext :614-627) | raw-SQL + jsonb-text | IT:841 | row shipped ahead of runner migration (entity doc :9-10) |
| 30-31 | status SQL ×3 → 3 row types → `StatusPageDto` (StatusFunctions.cs:31-57; `StatusPageProjection.cs:22-76`) | raw-SQL + pure helper | IT:3312 | |
| 32 | reconcile_drift SQL → `ReconcileDriftRow` → item DTO (ReconcileFunctions.cs:63-69) | raw-SQL + jsonb-text | IT:895 | |
| 33 | reconcile_apply_plan SQL ×3 → `ReconcileApplyPlanRow` (ReconcileFunctions.cs:89-92, :141-144, :180-183) | raw-SQL | approve/apply IT:3541-3944; **`GetReconcilePlan` endpoint itself: no test invokes it** (0% coverage, §5) | plan-serving envelope unpinned |
| 34 | plan jsonb → `PlanDoc/PlanStmt` + **positional `Values[0..9]`** (`v[5]`=sensitive, `v[9]`=spec_path; ReconcileFunctions.cs:195, :213-223, :338-339) | jsonb positional | IT:3673-3944 | index drift vs runner `computeApplyPlan` is silent — **#131 was exactly this** |
| 35 | spec_catalog SQL (21 aliases) → `SpecCatalogRow` → DTOs (SpecsFunctions.cs:36-75; DbContext :654-677) | raw-SQL + jsonb-text | IT:953 | widest alias surface after trust |
| 36-39 | entity → DTO hand maps: `Run→RunDto`/`TimelineEntryDto`, `RunStep/RunMetric/Flow/Channel→DTOs`, `Incident→IncidentDto`+detail, `Check→CheckSummaryDto/CheckDetailDto` (60+ fields) | hand map | MappingTests.cs (13-139), IT various; **`RunMetricDto` field values never asserted** (envelope-only) | compile-checked source side; drift risk is vs dashboard expectations |
| 40 | jsonb value-converter columns: `checks.{assertions,request_headers,auth,net_config,steps,redact_patterns}`, `channels.config`, `incidents.rca` (DbContext :261-281, :95-97, :361-363; options :706-710 camelCase/WhenWritingNull) | jsonb typed | IT:1796 round-trip; MappingTests.cs:109 (`generated_at`) | each is a writer(runner)/reader(api) contract; `rca.generated_at` snake_case leak lives here |
| 41 | `runs.trace_signals` jsonb ↔ `TraceSignalsDto` + `TraceExtractor.FromZip` (cross-language port of runner traceSignals.ts) | jsonb x-repo | **golden-guarded**: TraceSignalsGoldenParityTests + `.github/workflows/trace-parity.yml` | drifted once (`mutations`); now the best-guarded contract in the repo |
| 42 | audit before/after snapshots → `audit_log` jsonb (AuditWriter; DbContext :221-222) | jsonb write | IT:2530, AuditRedactionTests | |
| 43 | write-side runner mirrors: check_tags upsert ≙ tags.ts, check_locations ≙ locations.ts, create-time seeding, checks `ON CONFLICT` (TagsFunctions.cs:80-86, LocationsFunctions.cs:113-117, ChecksFunctions.cs:237-240, ReconcileFunctions.cs:234-240) | raw-SQL write x-repo | IT:2212, IT:1946, IT:1841, IT:3673 | CLAUDE.md mirror-pattern with cross-ref comments |
| 44 | **`POST /checks/{id}/run` → `run_requests`** (ChecksRunFunctions.cs:47-73; DbContext :133-144) | EF entity | **UNTESTED** — no test references it, and **`run_requests` is absent from `fixtures/schema.sql`** (OBSERVED: zero grep hits in the fixture) | the one live, provable snapshot-drift instance today |
| 45 | misc LINQ anonymous selects (AiInsights/LocationDiff/Routing/Editors) | LINQ | IT various | compile-checked, low risk |

### 2b. Why the class recurs, and what would structurally prevent it

**Diagnosis (OBSERVED):** the failure surface is not one mapper — it is 23 by-name alias chains + 17 jsonb contracts + 2 `SELECT *` sites + 1 positional-array contract, spread across two repos (runner writes, API reads) and consumed by a third (dashboard). Three structural facts let drift through:

1. **The schema snapshot is hand-maintained.** `fixtures/schema.sql` is a pg_dump with 8+ hand-appended "Added to the test snapshot" blocks (schema.sql:599-893). Nothing in CI compares it to the runner's migrations (OBSERVED: no workflow references it; it appears only in the test csproj copy step). Proof it drifts: `run_requests` (runner migration 0042) is missing, which is precisely why row 44 is untestable today.
2. **Wire-shape pinning stops at the top level.** IT:3158 `List_endpoint_top_level_shapes_are_pinned` (#123) pins bare-array vs top-level key sets for ~25 endpoints — the series-point-level drift class (`{date,value}` vs `{day,avgMs}`) would pass it.
3. **The cross-repo guards that exist are pattern-proven but applied to exactly one contract each.** Golden fixture: trace-signals only (trace-parity.yml). Predicate-parity InlineData: validation rules only. Both were built as fixes after the corresponding drift shipped.

**Prevention options evaluated against this codebase:**

| Option | What it catches here | What it misses | Cost |
|---|---|---|---|
| (a) Contract tests executing real SQL against a **drift-checked** schema snapshot | link 1→2 of every raw-SQL chain (23 boundaries), incl. `SELECT *` sites and missing tables/columns — the mechanism (Testcontainers + real handler classes) **already exists and covers 22/23 boundaries**; the missing piece is snapshot fidelity + the 2 untested boundaries | dashboard-side expectations (link 3→wire) | LOW — CI already checks out the runner repo in two workflows (grant-coverage.yml, trace-parity.yml) |
| (b) Source-generated mappers (Mapperly/Riok) | only row→DTO links (rows 3,5,7,9,…) — the **best-tested, compile-checked** link in the chain; a generator cannot see SQL aliases, `HasColumnName`, jsonb keys, or the dashboard | the actual historical bug sites (SQL/wire/jsonb), all 4 of them | HIGH — churn across 60+-field DTOs for the lowest-risk link |
| (c) Shared shape definitions (JSON Schema / TS types shared with dashboard+runner) | link 3 (wire) for all consumers | requires a three-repo artifact pipeline and dashboard buy-in; doesn't validate SQL→row | HIGH coordination |

**Recommendation (one, with migration path): option (a) — make the existing contract-test machinery trustworthy end-to-end.** The repo already has the right architecture (integration tests run the real handlers' real SQL on real Postgres); it recurs anyway because the schema snapshot and the wire pin are both shallow. Concretely:

1. **Schema-parity CI job** (new job in `grant-coverage.yml`, which already checks out `craigoley/synthwatch`): spin `postgres:16`, apply the runner's `db/migrations/*.sql` in order, `pg_dump --schema-only`, normalize, and diff against `fixtures/schema.sql` — fail with the diff on mismatch (or auto-regenerate the fixture as an artifact). This turns the snapshot from "hand-maintained, drifts silently" into "asserted every push". It would have caught `run_requests` (row 44) the day migration 0042 merged. Effort: one workflow job + ~50-line script; no code change.
2. **Deep-shape pin**: extend the #123 envelope test from top-level key sets to full-depth key-structure comparison (serialize seeded responses, strip values, `JsonNode.DeepEquals` against checked-in golden shape files — the exact mechanism trace-parity already uses). This catches the series-point and flat-vs-nested classes on the API side without dashboard coordination. Effort: MEDIUM — one test + ~25 golden files, seeded data already exists.
3. **Close the two uncovered boundaries** once (1) lands: add `run_requests` to the snapshot + an IT for `RunCheckNow` (also kills the §5 coverage zero), and an IT invoking `GetReconcilePlan`.
4. **Optional follow-up** for the two `SELECT *` sites (rows 4, 8): name the columns explicitly so a runner-side column addition/rename fails loudly in the parity-checked ITs instead of binding silently.

Not recommended now: (b) — it hardens the only link the compiler already helps with; (c) — right long-term, wrong first step; revisit after (a) is green, starting with the highest-churn report DTOs.

---

## 3. GRANTS FROM THE CONSUMER SIDE

Every table × verb this codebase's SQL actually uses (production code: `Functions/`, `Infrastructure/`, `Data/`; test SQL excluded — it runs as the Testcontainers superuser). All rows OBSERVED in source; one representative citation per verb.

### 3.1 The artifact: table × verb

| table | SELECT | INSERT | UPDATE | DELETE | evidence (representative) |
|---|:-:|:-:|:-:|:-:|---|
| access_requests | ✅ | ✅ | — | ✅ | S: AuthFunctions.cs:191, EditorsFunctions.cs:146; I: AuthFunctions.cs:195; D: EditorsFunctions.cs:176-178 (`ExecuteDeleteAsync`) |
| alert_routes | ✅ | ✅ | — | ✅ | S: RoutingFunctions.cs:36; I: RoutingFunctions.cs:153,158; D: RoutingFunctions.cs:152,157 |
| audit_log | — | ✅ | — | — | I: AuditWriter.cs:49-50 (no API read exists, despite the doc comment at SynthWatchDbContext.cs:207) |
| channels | ✅ | ✅ | ✅ | ✅ | S: ChannelsFunctions.cs:38; I: :63; U: fetch-mutate :86→99 (no syntactic marker); D: :134 |
| check_locations | ✅ | ✅ | — | ✅ | S: LocationsFunctions.cs:126; I: ChecksFunctions.cs:237-240, LocationsFunctions.cs:113-116, ReconcileFunctions.cs:245-248 (all `ON CONFLICT DO NOTHING`); D: LocationsFunctions.cs:117-119 |
| check_tags | ✅ | ✅ | **✅** | ✅ | S: TagsFunctions.cs:99 + raw joins (StatusFunctions.cs:37-55, ReportsFunctions.cs:159-161, every `?tag` filter); **I+U: TagsFunctions.cs:80-84 — `INSERT … ON CONFLICT (check_id, key) DO UPDATE SET value` needs INSERT *and* UPDATE**; D: TagsFunctions.cs:86-87 |
| checks | ✅ | ✅ | ✅ | ✅ | S: ChecksFunctions.cs:73 + dozens of raw joins; I: :213,228 and ReconcileFunctions.cs:234-240 (`ON CONFLICT (source_key) DO UPDATE` = I+U); U: PATCH :272→280, soft-delete :318-321, **plus runner-emitted `UPDATE checks SET …` executed verbatim via raw DbCommand (ReconcileFunctions.cs:289-296, :324-334)**; D: :314 (`?hard=true`) |
| daily_check_rollup | ✅ | — | — | — | ReportsFunctions.cs:466-497, :623-633 |
| deploys | ✅ | — | — | — | ReportsFunctions.cs:54-58 (42P01-tolerant) |
| editors | ✅ | ✅ | — | ✅ | S: EditorsFunctions.cs:68, AuthPrincipalService.cs:55; I: EditorsFunctions.cs:97-98; D: :123-124 |
| flow_manifest | ✅ | — | — | — | FlowsFunctions.cs:27 |
| incidents | ✅ | — | — | — | IncidentsFunctions.cs:49,108,154 + raw (ChecksFunctions.cs:46-53, StatusFunctions.cs:34-52, ReportsFunctions.cs:197-371, SpecsFunctions.cs:59-62) |
| locations | ✅ | — | — | — | LocationsFunctions.cs:30,102-103; SELECT-source inside INSERTs (ChecksFunctions.cs:239) |
| maintenance_windows | ✅ | — | — | — | ChecksFunctions.cs:471-476; ReportsFunctions.cs:540-602 (anti-joins) |
| otp_codes | ✅ | ✅ | ✅ | — | S: AuthFunctions.cs:62,99-102; I: :67-74; U: fetch-mutate :110-116 |
| reconcile_apply_plan | ✅ | — | ✅ | — | S: ReconcileFunctions.cs:89-92,141-144,180-183; U: :161-163, :253-255 |
| reconcile_drift | ✅ | — | — | — | ReconcileFunctions.cs:63-66 |
| red_tests | ✅ | — | — | — | ReportsFunctions.cs:382-388,437-443 |
| report_narratives | ✅ | — | — | — | ReportsFunctions.cs:702-708 |
| run_metrics | ✅ | — | — | — | ChecksFunctions.cs:399; ReportsFunctions.cs:592,614 |
| run_requests | ✅ | ✅ | — | — | S: ChecksRunFunctions.cs:62; I: :52-55 |
| run_steps | ✅ | — | — | — | RunsFunctions.cs:31 |
| runs | ✅ | — | — | — | ChecksFunctions.cs:78-377, ArtifactsFunctions.cs:40-94, ReportsFunctions.cs (many), StatusFunctions.cs:39, SpecsFunctions.cs:48-57 |
| sessions | ✅ | ✅ | ✅ | — | S: AuthPrincipalService.cs:42-43; I: AuthFunctions.cs:125-132; U: :150-151 (LastUsedAt), :169-170 (RevokedAt) |
| sla_availability_{24h,7d,30d,90d} (views) | ✅ | — | — | — | SlaFunctions.cs:30-42 (allowlisted `FromSqlRaw`), StatusFunctions.cs:46 |
| spec_catalog | ✅ | — | — | — | SpecsFunctions.cs:36-63 |
| tag_routes | ✅ | ✅ | — | ✅ | S: RoutingFunctions.cs:37,169; I: :163; D: :162 |
| test_send_requests | ✅ | ✅ | — | — | S: ChannelTestFunctions.cs:69; I: :46-47 |

Plus `SELECT 1` (no table) — HealthFunctions.cs:34.

**Postgres functions requiring EXECUTE (OBSERVED):** `slo_status(check_id, from, to)` — ChecksFunctions.cs:180, ReportsFunctions.cs:153; `slo_burn_status(check_id)` — ReportsFunctions.cs:156.

**Sequences (INFERRED):** 10 insert-target tables have DB-generated ids (`ValueGeneratedOnAdd` in the DbContext): checks, channels, alert_routes, tag_routes, test_send_requests, run_requests, otp_codes, sessions, access_requests, audit_log. If the runner's migrations declare these `serial`/`bigserial`, the API role needs `USAGE` on each `<table>_id_seq` ("permission denied for sequence" otherwise); if `GENERATED … AS IDENTITY`, table INSERT suffices — identity sequences are internal dependencies and need no separate grant (verified: [postgresql.org identity-columns docs](https://www.postgresql.org/docs/current/ddl-identity-columns.html)). Which form applies is decided in the runner repo's migrations — **not verifiable from this side** (OPEN QUESTION §7).

### 3.2 What the CI gate checks (`.github/workflows/grant-coverage.yml`)

- **`scripts/check-grant-coverage.mjs`** — Azure RBAC plane only: statically parses `infra/main.bicep` role assignments for the function app's MI and compares the `(roleId@scope)` set exactly (both directions) against `infra/required-grants.json → azureRbac`. No table×verb relevance.
- **`scripts/check-pg-grant-coverage.mjs`** — Postgres plane:
  - REQUIRED = `infra/required-grants.json → postgres.writes` (hand-maintained write allowlist; SELECT deliberately excluded, assumed covered by an ops-side `ALTER DEFAULT PRIVILEGES` per the manifest comment).
  - GRANTED = regex `GRANT <privs> ON <tables> TO "synthwatch-api"` over the **runner repo's** `db/migrations/*.sql` (checked out in the workflow), UNIONed with `postgres.opsBaseline` (currently `checks: INSERT,UPDATE,DELETE`).
  - Fails if any required (table, priv) is granted nowhere.
  - Secondary auto-catch: regex-scans this repo's `.cs` for `INSERT INTO / DELETE FROM / UPDATE … SET` — **after neutralizing `DO UPDATE SET` → `DO_CONFLICT` (script line 96)** — and fails if a raw-write table isn't in `postgres.writes`. Table membership only; verbs are not compared.

### 3.3 Gap analysis — what the gate cannot see from this side

1. **[MAJOR, CONFIRMED] `check_tags` UPDATE is required by code and structurally invisible to every CI layer.** Falsification run on all three legs: (i) `TagsFunctions.cs:80-84` is `INSERT … ON CONFLICT (check_id, key) DO UPDATE SET value = EXCLUDED.value` (OBSERVED); (ii) PostgreSQL requires UPDATE privilege on the updated column(s) when `ON CONFLICT DO UPDATE` is present (official docs: [postgresql.org/docs/current/sql-insert.html](https://www.postgresql.org/docs/current/sql-insert.html)); (iii) `infra/required-grants.json:95` lists `check_tags: ["INSERT","DELETE"]` — no UPDATE — and the scanner's `DO UPDATE SET` neutralization removes the only syntactic evidence, so the table passes on INSERT membership alone (OBSERVED script line 96). The Testcontainers suite runs as superuser, so tests can't catch it either. **Whether prod actually grants UPDATE on check_tags is unknowable from this repo** (runner migrations out of scope for this session) — if it doesn't, the first `PUT /checks/{id}/tags` that re-values an existing key 500s. OPEN QUESTION §7; the manifest fix (add `"UPDATE"`) is 1 word.
2. **Runner-emitted SQL executed verbatim is unscannable.** `ReconcileFunctions.cs:289-296, :324-334` execute plan `text` fetched from `reconcile_apply_plan` via raw `DbCommand` — `UPDATE checks SET …` strings that exist only in the database, never in C# source (corroborated independently: the `latest-all` analyzer run flags exactly these two sites as CA2100 "review query string passed to DbCommand.CommandText", §5). Covered today only because `checks: UPDATE` sits in `opsBaseline`; a future plan touching another table is invisible to the scanner.
3. **EF change-tracking writes are invisible by design** (acknowledged in the manifest comment): all Add/Remove/fetch-mutate+SaveChanges writes (channels UPDATE, otp_codes UPDATE, sessions UPDATE, checks PATCH/soft-delete, every Add/Remove) rely on the hand-maintained allowlist. A new EF write to a new table merges silently unless someone updates the manifest.
4. **Sequences are unmodeled.** No privilege vocabulary beyond the 4 DML verbs; the migration parser drops non-DML privileges and can't match `ALL SEQUENCES IN SCHEMA`. If any id column is `serial`, a missing `USAGE` 500s every insert with no CI signal.
5. **Function EXECUTE is unmodeled.** `slo_status`/`slo_burn_status` need EXECUTE; the migration parser deliberately skips `GRANT … ON FUNCTION`, and `postgres.writes` has no function slot. A missing EXECUTE breaks `GET /checks/{id}`, `/reports/slo`, `/reports/trust` paths.
6. **SELECT coverage is asserted nowhere.** All 15+ read-only tables/views rest on an `ALTER DEFAULT PRIVILEGES` claim in a JSON comment, never verified by CI — and default privileges only apply to objects created after they're set, by the role they're set for. The code already anticipates one such failure (`deploys` 42P01 guard); the other 14 would 500.
7. **Scanner regex blind spots (latent, none currently triggered):** schema-qualified/quoted table names, concatenated SQL, `MERGE`, and files outside `Functions/`, `Infrastructure/`, `Data/`.

**Inverse check (gate entries the code no longer uses): none.** Every `postgres.writes` row matched live code in §3.1. The manifest is allowlist-accurate except the missing `check_tags: UPDATE`.

---

## 4. RUNTIME CORRECTNESS AUDIT

### 4.1 Npgsql connection pooling under Managed Identity token expiry

**OBSERVED configuration** (`Infrastructure/PostgresDataSourceFactory.cs`): one singleton `NpgsqlDataSource` (`Program.cs:26`); connection string carries host/db/username only — deliberately no password (comment :32-33) — `SslMode=Require`, `MaxPoolSize=20` (:34-43); `UsePeriodicPasswordProvider` fetching a `DefaultAzureCredential` token for scope `https://ossrdbms-aad.database.windows.net/.default`, `successRefreshInterval: 50min`, `failureRefreshInterval: 5s` (:45-57).

**Docs-verified semantics** (sandbox note: npgsql.org/learn.microsoft.com fetches blocked by egress policy; verified via search-indexed excerpts of those pages + directly-fetched GitHub issues):

- The password/token is checked **only when a physical connection is opened**; established pooled connections are unaffected by later token expiry (Azure PG Entra concepts — a deleted Entra user "can still sign in until the token expires": auth happens at connection time — [learn.microsoft.com security-entra-concepts](https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-concepts); corroborated by [npgsql/npgsql#5163](https://github.com/npgsql/npgsql/issues/5163), where failures manifest on new opens with stale credentials, not on live connections).
- `UsePeriodicPasswordProvider` caches the password and re-invokes the callback on a timer; it is the pattern the Npgsql docs recommend for rotating cloud tokens, with a failure interval "much lower than the success interval" ([npgsql.org/doc/security.html](https://www.npgsql.org/doc/security.html); the canonical Azure example uses 55min/5s — this repo's 50min/5s matches).
- Entra token lifetimes: ~24h for service principals/managed identities, 1–4h for user tokens ([Aaron Powell / MS techcommunity walkthrough](https://techcommunity.microsoft.com/blog/appsonazureblog/azure-postgresql-entra-id-authentication-and-net/4158132)). 50min ≪ every lifetime class.
- The classic "silent hours-later failure" ([npgsql/npgsql#5163](https://github.com/npgsql/npgsql/issues/5163): works at boot, PAM/auth failures start appearing half a day later) is caused by a token captured once (static password, or pre-7.x `ProvidePasswordCallback` semantics) being reused for new physical opens after expiry. **This codebase is on the correct side of that failure: the periodic provider re-fetches long before any token class expires.** (OBSERVED config + docs-verified mechanism.)

**Residual risks (INFERRED, none observed failing):**

1. `failureRefreshInterval` only applies when the **callback throws**. If `DefaultAzureCredential` returns a cached token that the server then rejects at open (clock skew, revocation), Npgsql would keep offering it for up to 50 min of failed opens — there is no rejected-password fast path. Azure.Identity's proactive refresh (tokens re-fetched well inside their lifetime) makes this a narrow window; noting for completeness.
2. Pool math under scale-out: `MaxPoolSize=20` is per worker instance; Flex Consumption can run N instances → up to 20·N server connections against the Postgres SKU's `max_connections`. Not verifiable locally (rails) — §7 open question.
3. The file-header comment says the token has a "~1h lifetime" (`PostgresDataSourceFactory.cs:11-12`) — MI tokens are typically ~24h; the comment is conservative rather than wrong, but worth correcting to avoid future "why 50min?" confusion.
4. Two `DefaultAzureCredential` instances exist (DB-private at `PostgresDataSourceFactory.cs:45`; DI singleton at `Program.cs:30` for blob/ARM/AOAI/ACS) — two independent token caches. Cosmetic (one extra IMDS chain probe), see §4.5.

### 4.2 Async-over-sync / blocking

**Clean — zero findings (OBSERVED).** Full sweep of `Functions/`, `Infrastructure/`, `Data/`, `Program.cs` for `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, `Task.Run`, `Thread.Sleep`, `async void`, `lock`, sync file IO: zero product-code hits (the only `File.ReadAll*` are test fixtures, `TraceSignalsGoldenParityTests.cs:54,63`). The synchronous zip/NDJSON parsing in `TraceExtractor.cs:44-63` operates on an already-buffered `MemoryStream` (`ArtifactReader.cs:54-61` awaits `DownloadToAsync` first) — CPU-bound, not blocking IO.

### 4.3 Exception → HTTP status consistency

Shared machinery: all unhandled exceptions → RFC 9457 500 via `ExceptionHandlingMiddleware.cs:42-50`; handler 4xx/503 via `ApiResults` (§1.4). Every numeric/date query parse is `TryParse`-guarded → 400 (`Paging.cs:14-18`, `CursorPaging.cs:104-126`; zero `int.Parse`/`Convert.To*` in product code, OBSERVED); route ids use `{id:long}` constraints (non-numeric → routing 404). All 10 JSON-body endpoints catch `JsonException` → 400 "not valid JSON".

**Findings (ranked):**

1. **[MODERATE, CONFIRMED] Non-JSON `Content-Type` on any of the 10 body-reading endpoints → 500 instead of 400/415.** All body reads use `HttpRequest.ReadFromJsonAsync<T>` (`TagsFunctions.cs:52`, `ChecksFunctions.cs:193,262`, `LocationsFunctions.cs:66`, `ChannelsFunctions.cs:150`, `RoutingFunctions.cs:56`, `ReconcileFunctions.cs:136`, `EditorsFunctions.cs:87`, `ParseIntentFunctions.cs:33`, `AuthFunctions.cs:240`), which throws **`InvalidOperationException`** — not `JsonException` — when the content type is not JSON ("Unable to read the request as JSON because the request content type … is not a known JSON content type", documented `HttpRequestJsonExtensions` behavior — [learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.httprequestjsonextensions.readfromjsonasync)). Only `JsonException` is caught → shielded 500. Falsification: no middleware validates content type (`Program.cs:69-71`); the test suite always sets `application/json` (`IntegrationTests.cs:46,3001`) so it never exercises the miss. `curl -X POST -d 'x'` (curl's default `application/x-www-form-urlencoded`) reproduces it on any of the 10. Fix shape: `req.HasJsonContentType()` → 415, or widen the catch. Related prior prod signal: the ARM 415 lesson in CLAUDE.md shows plain-text POSTs do occur in this ecosystem's tooling.
2. **[MINOR] Silent swallow in reconcile apply:** `ReconcileFunctions.cs:260-264` bare `catch` rolls back and appends to `failed[]` with **no logging** — a persistently failing plan is undiagnosable server-side. It also catches `OperationCanceledException`, and `RollbackAsync(ct)` with a cancelled token can throw out of the catch. (OBSERVED)
3. **[MINOR] Duplicate-name status inconsistency:** channel duplicate → 400 (`ChannelsFunctions.cs:68-71`), check duplicate → 409 (`ChecksFunctions.cs:230-235`). Plus the off-envelope `DeleteChannel` 409 (§1.5). (OBSERVED)
4. **[INFO]** `ExceptionHandlingMiddleware.cs:27` has no `OperationCanceledException` filter — client aborts log as `Error "Unhandled exception"` (noise, not a bug). Deliberate degradations (uniform auth 202s, 42P01→empty deploys, malformed-jsonb→empty) are consistent and documented in code. (OBSERVED)

### 4.4 Cancellation token propagation

**Near-perfect (OBSERVED, mechanical sweep):** 65/66 endpoints accept `CancellationToken` (the exception, `GetSuggestedTagKeys`, is a synchronous constant — benign). Zero occurrences of `CancellationToken.None`/`default` in product code. All ~123 EF/SQL async call sites pass `ct`, including the middleware audit path (`AuthorizationMiddleware.cs:59,109,125`). `AoaiClient` is exemplary (linked CTS with 30s cap, timeout-vs-caller-cancel distinction, `AoaiClient.cs:105-159`); `ArtifactReader` passes `ct` to both blob paths (:58, :66); `ArmRunnerJobTrigger` passes `ct` plus a hard 15s `HttpClient.Timeout` (:41-56).

Gaps: `AuthFunctions.cs:240` `ReadBodyAsync<T>` calls `ReadFromJsonAsync<T>()` without `ct` (3 auth endpoints) — minor; middleware error-path `WriteAsJsonAsync` without `ct` — benign terminal writes. (OBSERVED) Nuance (INFERRED): handlers use the worker invocation token, not `HttpContext.RequestAborted`; whether a client disconnect cancels the invocation token depends on host plumbing.

### 4.5 Cold-start weight

Startup does registrations only — no warm-up, no eager IO, no heavy static init (only source-generated regexes + static `JsonSerializerOptions`; OBSERVED `Program.cs`, grep of static ctors). First request therefore pays, stacked: `DefaultAzureCredential` chain probe → ossrdbms token fetch → TCP+TLS to Postgres (`NpgsqlDataSource.Build()` does not pre-open connections — OBSERVED no `OpenConnection` anywhere) → EF model build over ~45 `DbSet`s (no compiled model — OBSERVED absence). On Flex Consumption every scale-from-zero instance repeats this. (Composition INFERRED from lazy wiring.)

Levers (report-only): EF compiled model; a warm-up that resolves `NpgsqlDataSource`; share the DI `TokenCredential` with `PostgresDataSourceFactory` (kills the second credential chain); `PublishReadyToRun` (unset in csproj). Per-request nits: `ReconcileFunctions.cs:187` allocates `JsonSerializerOptions` per call (its five siblings are `static readonly`); `AdminEmails()` re-parses env per call (cheap, arguably deliberate live-reconfig).

---

## 5. CODE HEALTH

### 5.1 Build & analyzers

- **Current gate** (`SynthWatch.Api.csproj:10-11`): `EnableNETAnalyzers` + `AnalysisLevel=latest-recommended`, CI builds `-warnaserror`. **OBSERVED: `dotnet build -warnaserror` → `0 Warning(s), 0 Error(s)`.** The fleet zero-warning bar holds.
- **Delta at `latest-all`** (OBSERVED: `dotnet build --no-incremental -p:AnalysisLevel=latest-all -p:TreatWarningsAsErrors=false`, warnings deduplicated by unique file:line): **719 additional warnings**:

| Rule | Count | Reading |
|---|---:|---|
| CA1515 (make types internal) | 249 | app-assembly noise for a Functions app; mass-churn, low value |
| CA2007 (ConfigureAwait) | 245 | no SynchronizationContext in the isolated worker → near-pure noise here |
| CA1062 (validate public args) | 82 | mostly DTO/helper publics; would dilute real validation |
| CA2227 (read-only collections) | 28 | DTO setters used by serializers — churn risk with jsonb converters |
| CA3001 (SQL injection) | 20 | **all on EF `FromSql`/`FromSqlInterpolated` `FormattableString` sites — parameter-bound, same false-positive class as the CodeQL lesson in CLAUDE.md** |
| CA1056/CA1054/CA1055 (URI strings) | 35 | contract-level string URLs (blob/proxy paths) — intentional |
| CA1002 (List<T> exposure) | 18 | DTOs |
| CA1308 (ToLowerInvariant) | 10 | normalization helpers — lowercase is the contract (emails, tags) |
| CA1812/CA1034/CA1031/CA1724/CA1307/CA1508/CA2000 | 22 | mixed; CA1031×7 are the deliberate degradation catches (§4.3) |
| CA2100 (DbCommand text) | 2 | **real by design**: the reconcile plan-text executor (`ApplyMissingAsync`/`ApplyChangedAsync`) — the §3.3 gap-2 sites, guarded by shape-guards |

Verdict (report-only): `latest-all` is not worth adopting wholesale; the two CA2100 hits are already the §3 finding, and CA3001 would need 20 suppressions for parameterized SQL. If anything, cherry-pick single rules (e.g. CA2016 forward-ct, already clean) via `.editorconfig`.

### 5.2 Tests & coverage

- Suite: **328 passed, 1 skipped (cross-repo golden, by design), 0 failed** with Docker; without Docker the 78 Testcontainers integration tests skip gracefully (`PostgresFixture.Available`, OBSERVED both runs).
- Coverage (OBSERVED, `dotnet-coverage` cobertura over the full suite): **69.4%** line coverage of the `SynthWatch.Api` assembly (4,771/6,871); **84.8% excluding the source-generated worker glue** (`GeneratedFunctionMetadataProvider`, `DirectFunctionExecutor` etc., 1,244 lines structurally never executed under test).
- **Untested endpoints (0 lines hit in their handler bodies; cross-checked — zero test-source references):**
  - `RunCheckNow` (`ChecksRunFunctions.cs`) — also blocked from integration testing because `run_requests` is missing from `fixtures/schema.sql` (§2, row 44)
  - `UpdateCheck` (PATCH /checks/{id}) — `CheckValidation.ApplyPatch` is unit-tested, the handler transaction/404/audit path is not
  - `DeleteCheck` (DELETE /checks/{id}, incl. `?hard=true`)
  - `GetCheckSuccessTrace`, `GetRunScreenshot` (artifact proxies; `GetRunTrace` + shared `ProxyAsync` are covered)
  - `GetReconcilePlan` (GET /reconcile/plan; the same SELECT is covered indirectly via approve/apply)
- Also 0%: the three middlewares (`AuthorizationMiddleware`, `ExceptionHandlingMiddleware`, `RequestLoggingMiddleware` — their decision logic lives in `AuthGate`/`ProblemResults`, which are at 92-100%, but the middleware glue itself, incl. the audit calls, is never executed under test), `AcsEmailSender`, `PostgresDataSourceFactory`.
- Weakest covered areas: `CheckValidation` 59% (unhit branches = patch/validation combinations), `ParseIntentFunctions` handler 87% but class-level 6.7% artifact of logger scaffolding, `ResolveSignalsAsync` 44% (the §recon A1 fallback path is only half-exercised).

### 5.3 Packages (report-only)

- `dotnet list package --outdated`: **API — only `Microsoft.ApplicationInsights.WorkerService 2.23.0 → 3.1.2`, which is the deliberate hard pin** (csproj comment documents the 3.x `TypeLoadException` worker crash and the 2026-06-22 outage; guarded by a dependabot ignore rule — do not bump). Tests — `Microsoft.NET.Test.Sdk 18.6.0 → 18.7.0` only.
- `dotnet list package --vulnerable --include-transitive`: **no vulnerable packages** in either project (OBSERVED, nuget.org source).

---

## 6. BOUNDARY CONTRACTS

### 6.1 EXPOSES — evidenced response shapes

Method: shapes taken from the DTO types actually passed to results (not from names). The 2026-06-28 catalog (`FINDINGS-REPORT.md` §C2) remains accurate for every endpoint that existed then, **except the deltas listed below** — per the diff-don't-rediscover rule this section documents (a) those deltas and (b) the endpoints added since (#139–#150). Serialization: default web camelCase everywhere; explicit `[JsonPropertyName]` where present matches camelCase (checked — no new snake_case anywhere; the only snake_case on the wire remains `rca.generated_at`).

**Deltas to the C2 catalog (OBSERVED in DTO source):**

1. **`GET /api/checks/{id}/runs`** no longer returns bare `CursorPage<RunDto>` — it returns **`RunsPage`** (`Dtos/RunDtos.cs:234`): same keys plus **`latestRunId: number|null`** (additive; newest run id ignoring window/cursor; null = no runs).
2. **`PerformanceReportDto.webVitals`** gained **three** fields in #147 (`Dtos/ReportDtos.cs:71-83`): `inpP75Ms: number|null`, `inpCount: number`, `resourceCount: number|null`. (The doc comment "No INP." at :70 is stale — code is authoritative.)
3. `GET /api/tags/suggested` (bare `string[]`) exists and is absent from C2.

**Endpoints added since C2 (full evidenced shapes; `s` = ISO-8601 string, `d` = "yyyy-MM-dd" string):**

| Endpoint | DTO (cite) | Wire shape |
|---|---|---|
| GET /reports/slo | `SloReportResponseDto` (SloReportDtos.cs:10,17,37) | `{window, fleet:{totalRuns,downRuns,budget,consumed,remaining,remainingPct?,insufficientData}, items:[{checkId,checkName,kind,target,totalRuns,downRuns,budget,consumed,remaining,remainingPct?,burnRate,burnState:"fast"\|"slow"\|"none",reportedBurn,insufficientData}]}` |
| GET /reports/mttr | `MttrReportResponseDto` (MttrReportDtos.cs:11-52) | `{window, fleet:{resolvedCount,openCount,totalIncidents,meanSeconds?,medianSeconds?,mttdProxySeconds?,insufficientData}, items:[…same per check…], classification:[{classification,count,pctOfTotal}], trend:[{bucketStart:s,resolvedCount,meanSeconds?}]}` |
| GET /reports/incident-breakdown | `IncidentBreakdownDto` (ReportDtos.cs:53,42) | `{window,total,classified,unclassified,realOutages,precision?,buckets:[{classification,count,pctOfTotal}]}` |
| GET /reports/trust | `TrustReportDto` (ReportDtos.cs:167,152,119,133,144) | `{window, monitors:[{checkId,checkName,sensitive,lastGreenAt:s?,lastRunAt:s?,runCount,retryCount,retryRate?,incidents:{total,realOutage,flakyTransient,selectorDrift,environmentRegional,perfRegression,unclassified},redTest:{captured,testedAt:s?,method:"executed-red-fixture"\|"attested-manual"\|null},specProvenance:{executedSha256?,specPath?},trust:"proven-live"\|"flaky"\|"unverified"\|"nominal"}]}` |
| GET /reports/trust/{checkId} | `TrustMonitorDetailDto` (ReportDtos.cs:179,172) | `{window, monitor:TrustMonitorDto, retrySeries:[{day:d,runCount,retryCount,retryRate?}]}` |
| GET /status | `StatusPageDto` (StatusPageDtos.cs:11,19,31) | `{window, properties:[{name,state:"up"\|"degraded"\|"down"\|"unknown",checkCount,upCount,degradedCount,downCount,uptimePct?,buildingBaseline}], recentIncidents:[{property,title,openedAt:s,resolvedAt:s?,status,severity}]}` |
| GET /reports/egress | `EgressReportDto` (EgressReportDtos.cs:8,15,22) | `{window:"all"\|"24h", regions:[{location,currentIps:[string],distinctCount,ips:[{ip,runCount,firstSeen:s,lastSeen:s}]}]}` |
| GET /reports/deploys | `DeploysReportDto` (DeploysReportDtos.cs:8,15) | `{host,window,deploys:[{sha?,isSha,source,deployedAt:s}]}` |
| POST /checks/parse-intent | `ParseIntentDto` (ParseIntentDto.cs:9) | `{configured,note?,retryable,redirect?,reason?,valid,fields:CreateCheckRequest?,fieldErrors:{[field]:string},notes?}` |
| GET /reconcile/plan | `ReconcileApplyPlanDto` (ReconcileDto.cs:27,19) | `{items:[{sourceKey,driftType,status:"pending"\|"auto"\|"blocked"\|"noop",plan:<runner jsonb verbatim>,computedAt:s}],computedAt:s?}` |
| POST /reconcile/apply | `ReconcileApplyResultDto` (ReconcileDto.cs:37) | `{applied:[string],failed:[string],cap}` |
| POST /reconcile/trigger | `ReconcileTriggeredDto` (ReconcileDto.cs:62) | `{triggered:boolean}` |
| POST /channels/{id}/test | `ChannelTestAcceptedDto` (AlertingDtos.cs:21) | `{requestId}` |
| GET /channels/{id}/test/status | `ChannelTestStatusDto` (AlertingDtos.cs:28) | `{status:"pending"\|"sending"\|"delivered"\|"failed",detail?,requestedAt:s,completedAt:s?}` |

Non-DTO exposures (unchanged): the shared problem+json error envelope (§1.4); `Health`'s anonymous `{status,db}`; reconcile approve/reject's anonymous `{sourceKey,driftType,status}`; file streams for trace/screenshot proxies. `plan` and `detail` (reconcile) are `JsonElement` pass-throughs — their inner shape is runner-owned, not API-typed.

### 6.2 CONSUMES — tables/columns/functions read, with assumed shapes

The verb-level inventory is §3.1; the shape assumptions live in `Data/SynthWatchDbContext.cs` as explicit `HasColumnName` mappings (the API's entire belief about the runner's schema, since this repo has no migrations):

- **Entities (CRUD):** `checks` (Check + jsonb columns `assertions/request_headers/auth/net_config/steps/redact_patterns`, camelCase-keyed — DbContext :261-281), `channels` (`config` jsonb :95-97), `incidents` (`rca` jsonb :361-363 — keys camelCase **except `generated_at`**), `runs` (incl. `trace_signals` jsonb-as-string :299, `spec_provenance`, `egress_ip`, `retry_count`), `run_steps`, `run_metrics`, `check_tags`, `check_locations`, `locations`, `flow_manifest`, `editors`, `sessions`, `otp_codes`, `access_requests`, `audit_log` (write-only), `alert_routes`, `tag_routes`, `test_send_requests`, `run_requests`, `maintenance_windows`, `red_tests`, `daily_check_rollup`, `deploys`, `spec_catalog`, `report_narratives`, `reconcile_drift`, `reconcile_apply_plan`.
- **Keyless row shapes over raw SQL (23 boundaries, §2a):** each row type's `HasColumnName` set is a column-name contract with SQL this repo writes — except the **`SELECT *` sites**, where the column set is a contract with objects the *runner* owns: `slo_status(...)` → `SloStatusRow` (DbContext :384-399) and `sla_availability_{24h,7d,30d,90d}` views → `SlaAvailabilityRow` (:368-381).
- **Postgres functions:** `slo_status(check_id, from, to)` (RETURNS TABLE consumed positionally-by-name via `SELECT *`), `slo_burn_status(check_id)` (aliased columns `burn_state`, `reported_burn`).
- **Runner-written jsonb the API deserializes (shape contracts with runner writers):** `incidents.rca`, `runs.trace_signals` (golden-guarded), `reconcile_drift.detail` + `reconcile_apply_plan.plan` (passed through verbatim, but the **apply executor** additionally assumes `plan.statements[].values` positional semantics — `v[5]`=sensitive, `v[9]`=spec_path, `ReconcileFunctions.cs:213-223`), `checks.*` config jsonb, `channels.config`, `spec_catalog.tags`, `report_narratives.highlights/fact_pack`.

### 6.3 Internal disagreements (flagged)

1. **`rca.generated_at`** — the one snake_case key in an otherwise camelCase API (dual-use jsonb entity; fix requires a response-DTO split, prior A2).
2. **Near-duplicate series-point vocabularies persist** — `AvailabilityPointDto {ts,availabilityPct?,upRuns,downRuns}` (checks series) vs `AvailabilityPointDtoR {day,availabilityPct?,upCount,downCount}` (reports series) vs `LatencyPointDto {day,avgMs?}` vs `TrustRetryPointDto {day,…,retryRate?}` — four "point" shapes with three different day/ts keys and two up/down namings. This is the exact vocabulary split behind the historical `{date,value}` drift; consumers must special-case per endpoint. (OBSERVED in Dtos; not a bug today, a standing trap.)
3. **Flat vs nested latency stats** — `CheckSummaryDto.p50Ms/p95Ms` flat vs `PerformanceReportDto…latency{p50Ms,p95Ms,p99Ms,avgMs,sampleCount}` nested (prior C2 flag, still true).
4. **`spark` mini-series** `{t,d,s}` single-letter keys exist only as `[JsonPropertyName]` strings 3 hops from the SQL (`json_agg` aliases at `ChecksFunctions.cs:58` → JSON keys → `SparkPoint` attributes) — key-level pinning is absent (IT asserts non-empty only, §2 row 2).
5. **`WebVitalsDto` doc comment says "No INP."** while the type carries `inpP75Ms`/`inpCount` (`ReportDtos.cs:70-83`) — stale comment on a contract type.
6. **`latestRunId` envelope asymmetry** — `RunsPage` is `CursorPage<RunDto>` + one field, but incidents still return plain `CursorPage<IncidentDto>`; consumers paging both must branch. (Additive, deliberate — noting for the contract catalog.)
