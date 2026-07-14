> **✅ IMPLEMENTED — historical design record.** Shipped: see `Functions/LocationDiffFunctions.cs`
> (`GET /runs/{runId}/baseline-diff`) + `Infrastructure/LocationDiffInsight.cs`. Kept for design rationale only.

# Design — location comparison via trace-diff + AI insight

**Status:** recon + design + a **proven pure diff core** (`TraceSignalsDiff`, this PR). The endpoint + AI-over-diff
+ dashboard are designed here for follow-up — recon **reframed** the feature and surfaced a hard data
constraint, so they shouldn't be built until the reframe is confirmed.

**Goal:** when a monitor fails at one location but is healthy elsewhere, explain *why* — by diffing the failing
run's trace signals against a known-good run's, then asking gpt-5-mini about the delta.

---

## 1. Recon — readiness (OBSERVED)

- **#114 (persist per-run `trace_signals`) is NOT landed.** It exists only on the runner branch
  `feat/persist-trace-signals` (commit `5126b97`, migration `0040_trace_signals.sql` → `runs.trace_signals jsonb`).
  **The column does not exist in prod** (`information_schema` confirms) → not merged to main, not deployed, no
  rows populated. Its extraction is a faithful port of the API's `TraceExtractor` (same `TraceSignalsDto` schema),
  so once landed, persisted signals and on-demand `GetTraceSignals` are interchangeable.
- **★ Hard data constraint (the reframe):** OBSERVED over checks 74/77, 2 days — FAIL/ERROR runs have a trace
  **100%** of the time (76/76); **PASS runs have one 0%** of the time (0/18; 0 pass-with-trace in 7 days). A
  passing run leaves `trace_url` null (#113) and #114 would persist `trace_signals = null` for it too. **So
  "diff the failing-location run vs the passing-location run" is structurally impossible — the passing side has
  no signals.**
- The only reliably-present good-run signal for a check is the **success-trace baseline** (`checks.success_trace_url`,
  the per-check #113 slot, captured on a throttled success — location-agnostic). It has a trace → extractable now.

### → Reframe
The comparison is **"the failing run" vs "the monitor's last-known-good baseline"** — both always have traces.
When one region fails while the check is otherwise healthy, the baseline came from a passing run (often the other
region), so this *is* the location-difference the user wants — but framed honestly as **fail-vs-known-good**, not
literally "region A's run vs region B's run". (Two FAILING runs across regions can also be diffed when both fail.)

---

## 2. The diff (built + proven — `Infrastructure/TraceSignalsDiff.cs`)

Pure `Diff(TraceSignalsDto a, b, labelA, labelB) → TraceDiffDto`. Diffs two signal JSONs (the #114 payoff — not
two 18 MB zips). Reports:
- **Console:** errors `OnlyInA` / `OnlyInB` / `Shared` count, tagged site vs third-party.
- **Network:** per-side totals (requests / wire KB / third-party / failed), failed-host set deltas, third-party
  origin deltas.

### ★ Canonicalization (load-bearing — proven against the real eastus2/centralus pair, runs 844768 / 844774)
Real console messages carry per-run noise — random WebSocket ids, `?auid=…&gtm=…` query strings, ISO timestamps.
A naive text diff makes the **same** error (doubleclick CSP refusal, astutebot WebSocket failure) read as
different on every run → the diff is all-noise. `Canonicalize` lowercases + strips ISO timestamps + query strings
+ long id/hash tokens, so identical errors with different ids count as **shared**. Verified on the real pair: the
"only in eastus2 / only in centralus" lists collapse to shared once canonicalized; a genuine region-only error
(e.g. an `ECONNREFUSED` to a regional endpoint) still survives. Unit-tested.

---

## 3. Endpoint (designed, not built)

`GET /api/checks/{id}/location-diff?location=<failing>` (read/compute — no AOAI, so **open like trace-signals**):
1. Resolve the **failing run**: the latest `fail`/`error` run for the check (optionally filtered to `?location=`),
   which has a `trace_url`.
2. Resolve the **good run**: the check's success-trace baseline (`success_trace_url`). If absent, fall back to the
   most recent traced run from a *different* location/outcome.
3. Extract both via the shared `IArtifactReader` + `TraceExtractor` (both on main); **once #114 lands, read the
   persisted `trace_signals` instead — no blob download**.
4. Return `TraceDiffDto` with labels (`"eastus2 (fail)"` vs `"baseline (centralus, pass)"`) + the two run ids.

`POST /api/checks/{id}/location-diff/insight` (spends AOAI → **gated**, reuse the `AuthGate` verb-gate): runs the
diff, then feeds the **delta** (not two traces) to `AoaiClient` and returns an `AiInsightsDto`-shaped result.

---

## 4. AI over the diff (designed)

Reuse `AoaiClient` + the `AiInsights` honesty discipline. Prompt: *"A monitored check fails at {failing} but the
last known-good run ({good}) passed. Here is the DELTA between their trace signals. Explain the likely cause of
the location-specific failure."* Categorize: region-specific error / third-party blocked or absent in one region
/ a geo-CDN difference / a timeout only in one region / **looks transient (flakiness)**.

### ★ Flakiness-vs-real honesty (the user's angle)
The prompt must let the model say **"this looks transient"** when the delta is thin (e.g. a single WebSocket
timeout, no consistent region-only error) rather than invent a region root-cause. Carry the existing caveats
(SPA Web-Vitals best-effort, site-vs-third-party, "couldn't determine" over fabrication) and add: *the diff is
two single runs — a one-off difference may be flakiness, not a regional issue; say so when the signals are thin.*
Reuse the `Retryable`/`unavailable` + not-configured handling from the insight machinery (#104/#96).

---

## 5. Dashboard (designed)

On the per-location panel / the failing-location run: a **"Why is this location failing?"** action → calls the
diff endpoint, shows the delta (console errors only-here, third-party/failed-host deltas) + the AI insight, reusing
the insight-card UI + the transport/unavailable/not-configured states. Frame as a **comparison** ("eastus2 vs the
last good run") and show the flakiness caveat inline when present.

---

## 6. Recommended build sequence
1. **This PR** — the proven pure diff core (`TraceSignalsDiff` + `TraceDiffDto`) + tests. The data layer, dependency-free.
2. **(After the reframe is confirmed)** the `GET …/location-diff` endpoint — on-demand extraction now; switch to
   persisted `trace_signals` when **#114 lands** (the dependency to land first for the cheap path).
3. The gated `…/insight` AOAI layer (prompt + flakiness honesty).
4. The dashboard action + card.

> Net: recon reframed the feature (passing runs have no traces → fail-vs-baseline, not region-vs-region) and
> surfaced canonicalization as essential. The diff data layer is proven; the rest is clean once #114 lands and the
> reframe is accepted.
