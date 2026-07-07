# Pre-prod environment-partition — architecture scope

> **Note:** the base scope doc (PR #211) is not on `origin/main` at time of writing, so this file starts
> with the **adjudication appendix** below. When #211 lands, its scope content merges above this section.

**Context (OBSERVED):** no deployment-`environment` dimension exists on `checks`/`runs`/`check_locations`
today — the only `environment` token in the codebase is `environment_regional`, an RCA classification
(`ReportsFunctions.cs:385`, `ReportingRows.cs:97`), unrelated. So the S1 "default-exclude pre-prod" work
is forward-looking: it adds an env dimension (column/tag on `checks`) and, per report, either a
default-exclude (like the prod-health scores slo/mttr/trust) or a deliberate include.

---

## Adjudication — the 3 "to adjudicate" endpoints (region-health, egress, narrative)

**Principle applied:** does mixing a pre-prod check into *this specific* aggregation MISLEAD a prod-facing
consumer (→ exclude), or is the aggregation environment-agnostic by nature / would the consumer WANT
pre-prod here (→ include)? The S1 exclude-set is for prod-health **scores**; infrastructure **liveness/
network** signals are a different class.

### 1. `GET /reports/region-health` → **INCLUDE all environments** (do NOT add to the exclude-set)

**OBSERVED — what it computes** (`ReportsFunctions.cs:118-142`): one row per ENABLED region,
`max(check_locations.last_run_at)` per region. The docstring states its purpose explicitly (`:119-124`):
*"per-region freshness so a SILENTLY-DEAD region becomes visible … Freshness = MAX(check_locations
.last_run_at), which the runner advances at CLAIM time on every run (pass OR fail) — a pure liveness
signal."* Grouped by **region** (`l.name`), never by check.

**INFERRED — decision:** this is a **runner-liveness** signal, environment-agnostic by construction. The
region's runner advances `last_run_at` when it CLAIMS any check — prod or pre-prod, pass or fail.
Excluding pre-prod would make a region whose runner is alive-but-claiming-pre-prod read as
`stale`/`never_reported` → a **fabricated dead-region alarm**, the exact opposite of the endpoint's F-4
purpose. Include.

**The one fact that would flip it:** if region-health were re-consumed as *"prod-monitoring coverage per
region"* (is prod being watched from region X) rather than *"is the region's runner alive"*, then a
region kept fresh solely by pre-prod checks would falsely read as prod-covered → exclude. Its defined
purpose is runner liveness (dead-region detection), so as-built: **include.**

### 2. `GET /reports/egress` → **INCLUDE all environments** (do NOT add to the exclude-set)

**OBSERVED — what it computes** (`ReportsFunctions.cs:82-115`): per-`(location, egress_ip)` run counts
from `runs.egress_ip` (`GROUP BY location, egress_ip`, `:109`). Consumer, per docstring (`:83-84`): *"the
status-page egress panel (the Wegmans allowlist artifact + a live SNAT-rotation warning)."* The
correctness point is surfacing every distinct IP (`:85-86`) — *"a region's 2nd+ IP is SURFACED, never
deduped (distinctCount > 1 = a rotation)."*

**INFERRED — decision:** the egress IP is a property of the **runner's outbound network (SNAT)**, NOT the
monitored target — a pre-prod check egresses from the *same* per-region runner, so it observes the *same*
IPs. Both consumers need EVERY observed IP: (a) the **allowlist artifact** must be complete, or a prod
target could reject a runner request from an IP that only a pre-prod run happened to capture first;
(b) a **SNAT rotation** is a rotation regardless of which check observed it. Excluding pre-prod can only
*drop* real egress observations → an incomplete allowlist / a missed rotation. Include.

**The one fact that would flip it:** if pre-prod checks ran from a **separate runner / network** with
distinct egress IPs that must NOT enter the prod allowlist (a different subscription/subnet), then
including them would pollute the prod artifact → exclude. Today there is one shared runner per region
writing `runs.egress_ip` for all checks (no separate pre-prod egress), so: **include.**

### 3. `GET /reports/narrative` → **the API read needs NO change** (nothing to exclude here); the substantive exclude is **runner-side (fleet generation)**

**OBSERVED — what it does** (`ReportsFunctions.cs:731-770`): it is a **pass-through**, not an aggregation.
*"serve the LATEST runner-generated narrative (Layer 3) read-only. No generation here (the AOAI lives in
the runner)"* (`:732-733`). It does a single keyed lookup `WHERE scope_type={scope} AND scope_key={key}
AND window={window} ORDER BY generated_at DESC LIMIT 1` (`:756-762`) and returns the stored text +
`fact_pack` verbatim. There is **no per-check aggregation to filter** — the fleet narrative is one opaque,
pre-computed text blob.

**INFERRED — decision (two-part):**
- **`scope=monitor`** (`?key=<checkId>`) is per-check by construction — you are deliberately looking at
  ONE check (prod or pre-prod). Environment-exclude is N/A. **Include.**
- **`scope=fleet`** IS a prod-facing summary (feeds the dashboard status story) and SHOULD NOT fold
  pre-prod flakiness/incidents into a prod availability narrative — substantively it belongs in the
  exclude class alongside slo/mttr/trust. **BUT that exclusion cannot be applied in this API read** (you
  can't filter checks out of a finished paragraph). It must happen in the **runner's narrative
  GENERATION** — which checks feed the fleet `fact_pack`. → **The API S1b/S1c exclude-set PR does nothing
  for narrative;** file a runner-side item: "fleet narrative fact_pack excludes pre-prod checks."

**The one fact that would flip it:** if the fleet narrative is repositioned as an all-environments
engineering digest (not a prod status summary), then even the runner-side generation should include
pre-prod → include everywhere.

---

## Net for the S1b/S1c API exclude-set PR (zero open decisions)

| Endpoint | Class | API exclude-set action |
|---|---|---|
| slo / mttr / trust | prod-health **score** | EXCLUDE pre-prod (already the precedent) |
| **region-health** | runner **liveness** | **INCLUDE** — no change |
| **egress** | runner **network** | **INCLUDE** — no change |
| **narrative** (fleet) | prod summary, but **runner-generated** | **no API change**; exclude belongs to runner narrative generation (file runner item) |
| **narrative** (monitor) | per-check | INCLUDE (N/A) |

So the API exclude-set is: **slo, mttr, trust** (+ whatever else S1a already classified as a score) — and
region-health / egress / narrative are explicitly OUT of the API exclude-set, two because they are
environment-agnostic infrastructure signals and one because its exclusion is architecturally the runner's.
This closes the last "scoped → buildable" gap: the API PR has no remaining adjudication.
