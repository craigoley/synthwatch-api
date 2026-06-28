# SynthWatch API — Deep Analysis Findings Report

**Date:** 2026-06-28
**Repo:** craigoley/synthwatch-api (C# .NET Azure Functions)
**Scope:** DTO/contract audit, auth-gate audit, swallowed-error audit, AOAI cost/safety, blob handling, dead code/duplication

---

## Executive Summary

The API codebase is well-structured with strong fundamentals: fail-closed auth, centralized blob error handling, bounded AOAI retries. No critical security bypasses were found. The highest-value findings are:

- **1 confirmed prod-class bug** (transient blob error masquerading as 404 in baseline-diff)
- **1 high-severity cost risk** (no rate limiting on AOAI endpoints, auth gate defaults OFF)
- **1 casing inconsistency** that leaks snake_case into an otherwise all-camelCase API
- **10 duplication patterns** (2 auth-adjacent with divergence risk)
- **9 DTO contract observations** for the dashboard contract catalog

---

## SECTION A: CONFIRMED PROD BUGS

### A1. [HIGH] LocationDiffFunctions.ResolveSignalsAsync — transient blob error returns 404 instead of 503

**Status:** CONFIRMED BUG — same class as the prior "503-not-500" fix, missed in this one path.

**OBSERVED:** `Functions/LocationDiffFunctions.cs:114` — `if (blob.Status != ArtifactStatus.Ok) return null;` collapses `Unavailable` (transient 429/503) with `Missing`/`Gone` (permanent 404) into a single `null`. The caller at line 58–59 maps `null` → `ApiResults.NotFound("No trace to analyze for run {runId}.")` — HTTP 404.

**Contrast:** The BASELINE blob read in the same endpoint (lines 64–68) correctly distinguishes `Unavailable` → 503 from other non-Ok → 404. This is an internal inconsistency within one function.

**Prod impact:** When Azure Blob has a transient hiccup, the baseline-diff endpoint tells users "no trace to analyze" (404, non-retryable) for the failing run, even though the trace exists. The dashboard won't retry on 404.

**Falsification:** Could this be intentional? No — the baseline path in the same endpoint handles it correctly, and every other blob-consuming endpoint distinguishes transient from permanent. This is an oversight.

**Fix size:** ~5 lines. Return `ArtifactStatus` from `ResolveSignalsAsync` (or check it before the null branch). The pattern is already established in the same file at lines 64–68.

---

### A2. [MEDIUM] IncidentRca.generated_at — snake_case leak in an all-camelCase API

**Status:** CONFIRMED — observable on the wire in every incident response containing RCA data.

**OBSERVED:** `Data/Entities/IncidentRca.cs` has `[JsonPropertyName("generated_at")]` on one field, producing `generated_at` (snake_case) in JSON output. Every other field in the entire API is camelCase. This leaks into `IncidentDto.rca` and `IncidentDetailDto.rca` (both in `Dtos/RunDtos.cs`).

**Prod impact:** Any dashboard code parsing `rca.generatedAt` (camelCase, matching the convention) gets `undefined`. Must use `rca.generated_at` instead. A contract inconsistency that will confuse any new consumer.

**Falsification:** Could this be intentional for DB column mapping? The `[JsonPropertyName]` attribute controls JSON serialization, not DB mapping (EF Core uses `[Column]` or conventions). This is a naming error.

**Fix size:** 1 line — change `"generated_at"` to `"generatedAt"`.

---

## SECTION B: LATENT RISKS (not yet prod-broken, but will bite)

### B1. [HIGH] No rate limiting on AOAI endpoints; auth gate defaults OFF

**OBSERVED:** Neither `POST /api/runs/{id}/ai-insights` (`AiInsightsFunctions.cs:33`) nor `POST /api/runs/{runId}/baseline-diff` (`LocationDiffFunctions.cs:37`) has any per-user/per-check throttle. `AUTH_ENFORCEMENT_ENABLED` defaults to OFF (`AuthorizationMiddleware.cs:28-31`), meaning anonymous callers can hit AOAI endpoints when the flag isn't set.

**Cost exposure:** Each call costs ~$0.01–0.05. At 10 req/s for 1 hour = 36,000 calls = $360–$1,800. No circuit breaker.

**Prod impact:** Runaway spend from abuse or a tight retry loop. Even with auth ON, a legitimate editor could accidentally spam the endpoint.

**Falsification:** Is rate limiting handled at the infrastructure layer (API Management, Azure Front Door)? Possibly — but there's no evidence of it in this codebase, and the risk should be documented.

**Fix size:** MEDIUM. Add a per-check cooldown (e.g., 1 call/check/minute) in the endpoint handlers, or add an Azure API Management rate-limit policy.

---

### B2. [MEDIUM] Network request URLs sent to AOAI are unbounded

**OBSERVED:** `Infrastructure/TraceExtractor.cs:87` — `Url: url` assigned with no length cap. Up to 23 network request records carry full URLs. Console messages are capped at 200 chars and 40 messages, but URLs are not.

**Prod impact:** A monitored site with data URIs or huge query strings could inflate AOAI input tokens dramatically. Worst case: 23 URLs × 100KB = 2.3MB of URL text.

**Fix size:** 1 line — truncate URLs to ~500 chars in `TraceExtractor`.

---

### B3. [MEDIUM] AOAI parse failure content never logged

**OBSERVED:** `Infrastructure/AiInsights.cs:73-93` and `Infrastructure/LocationDiffInsight.cs:71-90` — both `Parse` methods catch `JsonException` and return `null`. Neither logs the exception or the model's response content. The callers return "unexpected response format" to the user but also don't log the content.

**Prod impact:** If the model starts returning a new format, there's no way to diagnose it from logs. You'd see "unexpected response format" in API responses but would need to reproduce the call.

**Fix size:** ~3 lines per caller — log the first N characters of the content when `Parse` returns null.

---

### B4. [MEDIUM] Auth enforcement defaults OFF — deployment checklist item

**OBSERVED:** `Infrastructure/AuthorizationMiddleware.cs:28-31` — `EnforcementEnabled()` returns `false` unless `AUTH_ENFORCEMENT_ENABLED` is explicitly `"true"` or `"1"`. When OFF, ALL write endpoints are effectively ungated (including AOAI-spending endpoints).

**Prod impact:** If the env var is missing or misconfigured in production, every POST/PUT/DELETE endpoint is open to anonymous callers.

**Falsification:** This is explicitly by design for phased rollout. But the default-OFF posture combined with AOAI cost exposure (B1) makes this a critical deployment concern.

**Fix size:** 0 code — verify the flag is ON in prod. Consider flipping the default to ON.

---

### B5. [LOW] Logout blocked for demoted users

**OBSERVED:** `POST /api/auth/logout` is NOT in the `UnauthWriteAllowlist` (`Infrastructure/AuthGate.cs`). Test at `AuthGateTests.cs:87` confirms it's gated. A user with a valid session whose role is `anonymous` (was an editor, then removed) gets 403 when trying to log out.

**Prod impact:** Minor UX issue — the session expires naturally (30d TTL) but can't be explicitly revoked by a demoted user. Arguably worse security posture (sessions that can't be revoked).

**Fix size:** 1 line — add `/auth/logout` to `UnauthWriteAllowlist`.

---

### B6. [LOW] Prompt injection via user-controlled data in AOAI prompts

**OBSERVED:** Check names, console messages, and network URLs are embedded directly into AOAI prompts (`AiInsights.cs:58-68`, `LocationDiffInsight.cs:57-67`) with no sanitization.

**Prod impact:** Low practical risk — `response_format: json_object` constrains output, and the system prompt is detailed. Impact limited to manipulating insight content, not exfiltration.

**Fix size:** Document as accepted risk, or add a sanitization pass.

---

## SECTION C: DTO / CONTRACT CATALOG

This is the reference for dashboard contract audits. All DTOs serialize as camelCase on the wire (via ASP.NET Core's default `JsonNamingPolicy.CamelCase`).

### C1. JSON Serialization Convention

**Global:** ASP.NET Core default — `JsonNamingPolicy.CamelCase` (`Program.cs` — no explicit override).

**Dual strategy (fragile):** ~50% of DTOs use explicit `[JsonPropertyName]` attributes; ~50% rely on the runtime default. Both produce camelCase today. If someone ever configures a non-camelCase policy, the un-annotated DTOs would break.

**DTOs WITH `[JsonPropertyName]`:** NarrativeDto, all ReportDtos, AlertingDtos, LocationDtos, SpecDto, EditorDtos, ReconcileDto, TagDtos, AuthDtos, plus SparkPoint/LocationDto in CheckDtos.

**DTOs WITHOUT `[JsonPropertyName]`:** AiInsightsDto, TraceSignalsDto, RunDtos (RunDto, RunStepDto, RunMetricDto, IncidentDto, etc.), LocationDiffDto, TraceDiffDto, RunNowDtos, CheckSummaryDto, CheckDetailDto, CheckMetricsDto, SloDto.

### C2. Complete DTO Shape Reference

#### Checks

**`GET /api/checks` → `CheckSummaryDto[]`** (no envelope)
```
{
  id: long, name: string, kind: string, targetUrl: string,
  flowName: string?, method: string, expectedStatus: int,
  intervalSeconds: int, timeoutMs: int, failureThreshold: int,
  severity: string, enabled: bool, lighthouseEnabled: bool,
  lastRunAt: DateTimeOffset?, createdAt: DateTimeOffset,
  currentStatus: string, currentHealth: string,
  lastRunId: long?, lastDurationMs: int?, lastHttpStatus: int?,
  hasOpenIncident: bool,
  p50Ms: double?, p95Ms: double?, runs24h: int,
  spark: [{ t: DateTimeOffset, d: int?, s: string }],  // [JsonPropertyName]
  openIncidentCount: int, maxOpenSeverity: string?,
  certExpiryWarnDays: int, lastCertDaysRemaining: int?,
  assertions: [{ source, comparison, target?, expected? }],
  requestHeaders: { string: string }?,
  requestBody: string?, auth: { string: string }?,
  netConfig: { recordType?, expectedValue?, port? }?,
  steps: [{ name, method?, url, headers?, body?, auth?, assertions?, extract? }]?,
  locations: [{ location: string, status: string }],
  tags: [{ key: string, value: string }],  // [JsonPropertyName]
  sourceKey: string?, specPath: string?
}
```

**`GET /api/checks/{id}` → `CheckDetailDto`**
```
{
  id: long, name: string, kind: string, targetUrl: string,
  flowName: string?, method: string, expectedStatus: int,
  intervalSeconds: int, timeoutMs: int, failureThreshold: int,
  severity: string, enabled: bool, lighthouseEnabled: bool,
  bodyMustContain: string?,
  lighthouseIntervalSeconds: int?, lighthouseFormFactor: string,
  perfBudgetLcpMs: int?, perfBudgetTransferBytes: long?,
  certExpiryWarnDays: int,
  currentStatus: string, currentHealth: string,
  slo: { target: float, budget: decimal, consumed: long,
         remaining: decimal, burnRate: decimal,
         fastBurn: bool, slowBurn: bool }?,
  recentRuns: RunDto[],
  assertions: [...], requestHeaders: {...}?, requestBody: string?,
  auth: {...}?, netConfig: {...}?, steps: [...]?,
  tags: [{ key, value }],
  sourceKey: string?, specPath: string?,
  successTraceAt: DateTimeOffset?
}
```

**FLAG: FLAT vs NESTED LATENCY** — `CheckSummaryDto` has flat `p50Ms`/`p95Ms`. `PerformanceReportDto` has nested `latency: { sampleCount, avgMs, p50Ms, p95Ms, p99Ms }`. The summary lacks `avgMs`, `p99Ms`, `sampleCount`.

#### Runs

**`GET /api/checks/{id}/runs` → `CursorPage<RunDto>`**
```
{
  items: [{
    id: long, checkId: long, status: string,
    startedAt: DateTimeOffset, finishedAt: DateTimeOffset?,
    durationMs: int?, httpStatus: int?, errorMessage: string?,
    failedStep: string?, screenshotUrl: string?,
    certDaysRemaining: int?, traceUrl: string?,
    location: string  // never null, defaults to "default"
  }],
  nextCursor: string?, pageSize: int
}
```

**`GET /api/runs/{id}/steps` → `RunStepDto[]`**
```
[{
  id: long, runId: long, stepIndex: int, name: string,
  status: string, durationMs: int, errorMessage: string?,
  startedAt: DateTimeOffset
}]
```

**`GET /api/checks/{id}/metrics` → `PagedResult<RunMetricDto>`**
```
{
  items: [{
    runId: long, capturedAt: DateTimeOffset,
    ttfbMs: int?, domContentLoadedMs: int?, loadEventMs: int?,
    fcpMs: int?, lcpMs: int?, transferBytes: long?,
    resourceCount: int?, domNodeCount: int?, jsHeapBytes: long?,
    cpuTimeMs: int?, layoutCount: int?, recalcStyleCount: int?,
    cls: double?, inpMs: int?
  }],
  page: int, pageSize: int, total: long
}
```

#### Incidents

**`GET /api/incidents` → `CursorPage<IncidentDto>`**
```
{
  items: [{
    id: long, checkId: long, status: string, severity: string,
    openedAt: DateTimeOffset, resolvedAt: DateTimeOffset?,
    openedRunId: long?, resolvedRunId: long?,
    consecutiveFailures: int, summary: string?,
    checkName: string?, checkKind: string?,
    rca: {
      classification: string?, confidence: string?,
      observed: string[]?, inferred: string[]?,
      summary: string?, signature: string?,
      model: string?, cached: bool?,
      generated_at: string?  // ⚠️ SNAKE_CASE — see A2
    }?
  }],
  nextCursor: string?, pageSize: int
}
```

**`GET /api/incidents/{id}` → `IncidentDetailDto`**
```
{
  id: long, checkId: long, checkName: string?, checkKind: string?,
  status: string, severity: string,
  openedAt: DateTimeOffset, resolvedAt: DateTimeOffset?,
  durationSeconds: double?,  // ⚠️ null while incident is open
  consecutiveFailures: int, summary: string?,
  rca: { ... same as above ... }?,
  perLocation: [{ location: string, status: string }],
  timeline: [{
    runId: long, status: string, startedAt: DateTimeOffset,
    durationMs: int?, httpStatus: int?, errorMessage: string?,
    failedStep: string?, screenshotUrl: string?,
    traceUrl: string?, location: string
  }],
  recurrence: [{
    id: long, openedAt: DateTimeOffset,
    resolvedAt: DateTimeOffset?, status: string, summary: string?
  }]
}
```

#### Trace Signals & AI Insights

**`GET /api/runs/{id}/trace-signals` → `TraceSignalsDto`**
```
{
  targetHost: string?,
  network: {
    totalRequests: int, wireKb: long, thirdPartyCount: int,
    failed: [{ url, status, resourceType, timeMs, waitMs, size, wire, encoding, thirdParty }],
    slowest: [...], largest: [...], uncompressed: [...],
    topThirdParties: [{ host, count, kb }]
  },
  console: {
    messages: [{ level, origin, text }],
    droppedInfoLog: int, droppedExtensionNoise: int
  }
}
```

**`POST /api/runs/{id}/ai-insights` → `AiInsightsDto`**
```
{
  configured: bool,
  summary: string?,
  performance: [{ title, detail, severity, confidence, evidence? }],
  network: [...], errors: [...], suggestions: [...],
  caveats: string[],
  note: string?,
  retryable: bool
}
```

#### Baseline Diff

**`POST /api/runs/{runId}/baseline-diff` → `LocationDiffDto`**
```
{
  configured: bool, note: string?, retryable: bool,
  failing: { runId: long, location: string?, status: string },
  baseline: { source: string, capturedAt: DateTimeOffset?, location: string? },
  diff: {
    labelA: string, labelB: string,
    console: {
      onlyInA: [{ level, origin, text }],
      onlyInB: [...], shared: int
    },
    network: {
      totalRequestsA: int, totalRequestsB: int,
      wireKbA: long, wireKbB: long,
      thirdPartyCountA: int, thirdPartyCountB: int,
      failedCountA: int, failedCountB: int,
      failedHostsOnlyInA: string[], failedHostsOnlyInB: string[],
      thirdPartyOnlyInA: [{ host, count, kb }],
      thirdPartyOnlyInB: [...]
    }
  },
  insight: {
    summary: string, likelyCause: string, confidence: string,
    isFlaky: bool, findings: [{ title, detail, severity, confidence, evidence? }],
    caveats: string[]
  }?
}
```

#### Reports

**`GET /api/reports/availability` → `AvailabilityReportDto`**
```
{
  window: string, groupBy: string?,
  groups: [{
    group: string?, availabilityPct: decimal?,
    upCount: long, downCount: long, totalCount: long,
    downtimeMinutes: decimal, incidentsOpened: long,
    checks: [{ checkId, checkName, availabilityPct?, upCount, downCount, downtimeMinutes, incidentsOpened }],
    series: [{ day: DateOnly, availabilityPct?, upCount, downCount }]
  }]
}
```

**`GET /api/reports/performance` → `PerformanceReportDto`**
```
{
  window: string, groupBy: string?,
  groups: [{
    group: string?,
    latency: { sampleCount: long, avgMs: double?, p50Ms: int?, p95Ms: int?, p99Ms: int? },
    webVitals: { sampleCount: long, lcpP75Ms: int?, fcpP75Ms: int?, ttfbP75Ms: int?, clsP75: double? }?,
    checks: [{
      checkId, checkName,
      latency: { sampleCount, avgMs?, p50Ms?, p95Ms?, p99Ms? },
      webVitals: { ... }?
    }],
    series: [{ day: DateOnly, avgMs: double? }]
  }]
}
```

**`GET /api/reports/narrative` → `NarrativeDto`**
```
{
  scope: string, key: string, window: string,
  headline: string, body: string,
  highlights: string[], generatedAt: DateTimeOffset,
  stale: bool, model: string?, factPack: JsonElement
}
```

#### SLA & Availability Series

**`GET /api/sla` → `SlaResponseDto`**
```
{
  window: string,
  fleet: { completedRuns, upRuns, downRuns, availabilityPct?, insufficientData },
  items: [{
    checkId, checkName, kind, windowFrom, windowTo,
    completedRuns, upRuns, downRuns, availabilityPct?, insufficientData
  }]
}
```

**`GET /api/checks/{id}/availability-series` → `AvailabilitySeriesDto`**
```
{
  window: string, bucket: string,
  points: [{ ts: DateTimeOffset, availabilityPct?, upRuns, downRuns }]
}
```

**FLAG: NEAR-DUPLICATE TYPES** — `AvailabilityPointDto` (ts/upRuns/downRuns) vs `AvailabilityPointDtoR` (day/upCount/downCount) serve similar purposes with different field names.

#### Alerting & Channels

**`GET /api/channels` → `ChannelDto[]`**
```
[{ id, name, type, config: { to: string[]?, url: string?, authHeader: string? }, enabled }]
```

**`GET /api/routing` → `RoutingDto`**
```
{
  severity: { [key]: { channelIds: long[] } }?,
  perCheck: { [key]: { channelIds: long[] } }?,
  tagRules: [{ tagKey, tagValue, channelId }]?
}
```

**`GET /api/notifications/health` → `NotificationsReadinessDto`**
```
{ channelsConfigured, routingConfigured, transportConfigured?, detail }
```

#### Other

**`POST /api/checks/{id}/run` → `RunNowAcceptedDto`** — `{ requestId: long }`

**`GET /api/flows` → `FlowDto[]`** — `[{ name, description?, entryUrlHint?, updatedAt }]`

**`GET /api/locations` → `LocationsResponse`** — `{ locations: [{ name, enabled }] }`

**`GET /api/specs` → `SpecCatalogDto`** — `{ items: [...18 fields...], probedAt? }`

**`GET /api/reconcile/drift` → `ReconcileDriftDto`** — `{ items: [{ sourceKey, driftType, detail: JsonElement, detectedAt }], detectedAt? }`

**`GET /api/tags` → `TagsInUseResponse`** — `{ tags: [{ key, value, count }] }`

**`GET /api/health` → anonymous object** — `{ status, db }` (⚠️ no DTO — see D3)

**Auth DTOs:** `MessageDto { message }`, `VerifyResponseDto { token, email, role, expiresAt }`, `MeDto { email, role }`, `EditorDto { email, addedBy, addedAt }`, `AccessRequestDto { email, requestedAt, count }`

---

## SECTION D: CLEANUP / QUALITY

### D1. [MEDIUM] Auth logic duplicated in AuthFunctions — divergence risk

**OBSERVED:** `Functions/AuthFunctions.cs` has private copies of `AdminEmails()` (line 220) and `ResolveRoleAsync()` (line 209) that duplicate the canonical versions in `Infrastructure/AuthPrincipalService.cs` (lines 62, 51). If role-resolution logic changes in one but not the other, auth behavior diverges.

**Fix size:** Delete private copies, inject `IAuthPrincipal`. ~10 lines changed.

### D2. [LOW-MEDIUM] 10 duplicated logic patterns across the codebase

All verified with file:line evidence:

| Pattern | Occurrences | Risk | Fix |
|---------|-------------|------|-----|
| `AdminEmails` + `ResolveRoleAsync` | 2× (AuthFunctions + AuthPrincipalService) | HIGH — auth divergence | Inject IAuthPrincipal |
| `Str`/`Norm`/`Strings` JSON helpers | 2× (AiInsights + LocationDiffInsight) | MEDIUM — AOAI parse divergence | Extract shared helper |
| `targetHost` extraction | 3× (AiInsights, Artifacts, LocationDiff Functions) | LOW — trivial one-liner | Extract to TraceExtractor |
| `SafeStringList` | 2× (Reports + Specs Functions) | LOW | Extract shared helper |
| `SafeJson` | 2× (Reports + Reconcile Functions) | LOW | Extract shared helper |
| `TryReadEmail` | 2× (Auth + Editors Functions) | LOW | Use AuthTokens.NormalizeEmail |
| `ClientIp` | 2× (AuthFunctions + AuthorizationMiddleware) | LOW | Extract shared helper |
| `ArtifactStatus` switch | 3× (Artifacts + AiInsights Functions) | LOW | Extension method |
| Cache-Control headers | 7× (various Functions) | COSMETIC | Extension method |

### D3. [LOW] Health endpoint uses anonymous object — no DTO

**OBSERVED:** `Functions/HealthFunctions.cs` returns `new { status = "healthy", db = "up" }` — the only endpoint without a declared DTO. Not a bug, but makes the contract undiscoverable from DTO definitions.

### D4. [LOW] NotificationsFunctions uses `new OkObjectResult(...)` instead of `ApiResults.Ok(...)`

**OBSERVED:** `Functions/NotificationsFunctions.cs` — inconsistent with every other endpoint. Functionally identical.

### D5. [LOW] Malformed persisted JSON silently swallowed (3 locations, no logging)

- `Functions/LocationDiffFunctions.cs:110` — malformed `TraceSignals` JSON, empty catch
- `Functions/ReportsFunctions.cs:270,277` — malformed narrative `highlights`/`factPack`
- All degrade gracefully but produce no log entry for corrupt data

### D6. [LOW] AOAI non-2xx response body not logged

**OBSERVED:** `Infrastructure/AoaiClient.cs:127-131` — HTTP status code is logged but response body is discarded. AOAI 400/429 responses often contain diagnostic detail (rate-limit reset, content-policy violation).

### D7. [INFO] `POST /checks/{id}/run` missing from AuthGateTests.Writes

**OBSERVED:** `tests/SynthWatch.Api.Tests/AuthGateTests.cs:14-28` — the `Writes` test array doesn't include the run-trigger endpoint. Not a security gap (fail-closed covers it) but a test coverage gap.

---

## TOP 10 BY VALUE/EFFORT

| Rank | ID | Category | Severity | Fix Size | Description |
|------|-----|----------|----------|----------|-------------|
| 1 | A1 | Prod Bug | HIGH | ~5 lines | Blob transient error → 404 in baseline-diff failing-run path |
| 2 | A2 | Prod Bug | MEDIUM | 1 line | `generated_at` snake_case leak in IncidentRca |
| 3 | B1 | Risk | HIGH | MEDIUM | No rate limiting on AOAI endpoints |
| 4 | B4 | Risk | MEDIUM | 0 code | Verify AUTH_ENFORCEMENT_ENABLED=true in prod |
| 5 | D1 | Cleanup | MEDIUM | ~10 lines | Auth logic duplication in AuthFunctions (divergence risk) |
| 6 | B2 | Risk | MEDIUM | 1 line | Unbounded URL lengths sent to AOAI |
| 7 | B3 | Risk | MEDIUM | ~6 lines | AOAI parse failures lose model response content |
| 8 | B5 | Risk | LOW | 1 line | Logout blocked for demoted users |
| 9 | D2 | Cleanup | LOW-MED | ~30 lines | targetHost 3× + Str/Norm/Strings 2× duplication |
| 10 | D5 | Cleanup | LOW | ~6 lines | Malformed persisted JSON silently swallowed |

---

## AUTH-GATE SUMMARY (all 51 endpoints)

**Architecture:** Fail-closed-by-verb. All GET/HEAD open. All POST/PUT/PATCH/DELETE denied unless allowlisted or caller has editor/admin session. Admin-only for `/editors` writes.

**Verdict:** STRONG. No bypasses found. Every compute-spending endpoint (AOAI, run-trigger) is correctly gated to editor/admin.

**One design note:** GET endpoints for artifacts (traces, screenshots) serve potentially sensitive site content without auth — acceptable for internal tooling, warrants review if internet-facing.

---

## BLOB HANDLING SUMMARY

**Architecture:** Centralized in `ArtifactReader.cs` with 4-state `ArtifactStatus` enum (Ok/Missing/Gone/Unavailable). All `RequestFailedException` caught in one place. 503-not-500 pattern applied consistently in 4 of 5 call sites.

**One gap:** `ResolveSignalsAsync` (see A1) — the 5th call site collapses Unavailable into null → 404.

---

## AOAI SAFETY SUMMARY

**Enforced caps:** Console ≤40 messages × 200 chars. max_completion_tokens = 16,000 (configurable via env var). 30s timeout per attempt. 1 retry with 750ms backoff on transient errors only.

**Gaps:** No per-user/per-check rate limit (B1). Network URLs unbounded (B2). Parse failure content not logged (B3).
