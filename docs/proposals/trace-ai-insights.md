# Design spike — "Get AI insights" from a run's Playwright trace

**Status:** recon + design + **proven extraction prototype**. Not yet built — the full feature spans 2-3 PRs
across two repos plus a deploy prerequisite (a managed-identity role assignment), so it does **not** scope to
one clean PR. This doc is for Craig to greenlight the build slices.

**The feature:** a "Get AI insights" button on the run/trace detail view that sends *extracted, filtered* trace
signals to `gpt-5-mini` and returns actionable insights about the **monitored site** — performance, network
efficiency, Lighthouse-style suggestions, and genuine site console errors. This extends RCA from *"why did my
check fail"* to *"how could the monitored site be better."*

---

## 1. Recon — what exists today

### The AI path (to reuse, not reinvent)
RCA already calls AOAI; mirror it.
- **Where:** `runner/rca.ts` (proven) + `runner/aoai.ts` (the shared transport the narrative job uses).
- **Auth:** **Managed Identity** — `DefaultAzureCredential` (pinned to `AZURE_CLIENT_ID` for the runner's
  user-assigned MI), token scope `https://cognitiveservices.azure.com/.default`. **No API key.**
- **Transport:** native `fetch` to `${AZURE_OPENAI_ENDPOINT}/openai/deployments/${deployment}/chat/completions?api-version=${AZURE_OPENAI_API_VERSION}`,
  `Authorization: Bearer <token>`. Env: `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT=gpt-5-mini`,
  `AZURE_OPENAI_API_VERSION` (RCA defaults `2024-10-21`; Craig's note says `2025-04-01-preview` — use that).
- **Call shape:** `messages:[{system},{user}]`, `max_completion_tokens` (gpt-5-mini is a *reasoning* model —
  hidden reasoning tokens eat the budget, so size generously, ~4000), `response_format:{type:'json_object'}`
  (JSON mode), optional `reasoning_effort`. Tolerant JSON extraction (`extractJson` strips fences).
- **Discipline:** 30s timeout (AbortController), **non-fatal** (any failure → `null`, never blocks), no retries,
  cache by signature.

> ★ The proven AI path lives in the **runner (TypeScript)**. The **C# API has never called AOAI.** "Reuse" here
> means *mirror the pattern* (MI token → REST → JSON mode → gpt-5-mini) in C#, OR route through the runner. See
> §5 for where the new call should live and the **deploy prerequisite** it implies.

### The trace path
- **Capture (`runner/index.ts`):** `context.tracing.start({ screenshots:true, snapshots:true, sources:false })`;
  the zip is kept **only on failure**. Playwright **1.61.0**.
- **Store (`runner/artifacts.ts`):** uploaded to blob `synthwatch-artifacts/traces/run-<id>-<ts>.zip`; the URL
  is written to `runs.trace_url`.
- **Serve (`Functions/RunsFunctions.cs`):** `GET /api/runs/{id}/trace` streams the blob via the **API's managed
  identity** (`BlobClient(uri, _credential)`), validated to `*.blob.core.windows.net`. **The API already has
  the trace bytes + the MI to read them.**
- **Parsing:** **none anywhere.** No code unzips or parses a trace. Greenfield. (No zip dep needed in C# —
  `System.IO.Compression` is in the framework.)

### What's inside the zip (verified against the real prod trace, run 844486)
18.6 MB zip containing:
- **`trace.network`** — newline-delimited JSON, one `resource-snapshot` per request (HAR-shaped): `request`
  (method/url/headers), `response` (`status`, `content.{size,mimeType,compression}`, `bodySize`,
  `_transferSize`, headers), `timings` (`dns/connect/ssl/send/wait/receive` ms), `_resourceType`
  (document/script/stylesheet/image/fetch/font…), `serverIPAddress`. **This is the richest, most reliable signal.**
- **`trace.trace`** — NDJSON of actions + **`console`** events (`messageType` error|warning|info|log, `text`,
  `location.url`) + screencast frames + snapshots.
- **`resources/`** — screenshots (jpeg) + css/js bodies.

---

## 2. The extraction (proven — `prototype/extract_trace.py` run on run 844486)

You **cannot** send the model the 18 MB trace. Extract a **compact, filtered** structured summary server-side
and send *that* (bounded tokens). The prototype parses the real trace and emits:

```
NETWORK: 561 requests, 11431 KB on wire, 287 third-party, 0 failed

── slowest requests (total ms) ──
   1499ms (wait -1)  200 script     3p  blob:https://www.wegmans.com/686a9e3b-…
   1026ms (wait 613) 200 script         https://www.wegmans.com/_next/static/chunks/05qfr1~43mehr.js
   1026ms (wait 703) 200 script         https://www.wegmans.com/_next/static/chunks/0dt.ho9l6o54k.js

── largest payloads (uncompressed bytes) ──
  2205KB wire=2206KB media   enc=none   https://images.wegmans.com/is/content/wegmanscsprod/7452251-…
   507KB wire=49KB  fetch    enc=gzip   https://www.wegmans.com/api/stores
   285KB wire=285KB image    enc=none   https://images.wegmans.com/is/image/wegmanscsprod/8119385-…

── failed requests (>=400) ──  (none this run)

── top third-party origins (count, KB on wire) ──
   70 reqs 6663KB  images.wegmans.com
   15 reqs  324KB  bot.emplifi.io
    2 reqs  284KB  www.googletagmanager.com

CONSOLE: kept 20 (error/warning), dropped 72 info/log chatter, 0 extension-noise
  [error|site] component:SiteHeaderSearch:helpers Invalid discovery pages storage data
  [error|site] Refused to execute script from 'https://di.rlcdn.com/...' because its MIME type ('image/gif')…
  [warning|site] The resource .../_next/static/chunks/0.nt4tbvgypbc.css was preloaded but not used…
  [error|3p] WebSocket connection to 'wss://realtime-c.astutebot.com/...' failed…
```

That summary is **a few hundred tokens** vs 18 MB — bounded cost. Note the search-header console error is
directly relevant to this *search* check: real, actionable signal RCA's failure-only view never surfaced.

### The signals extracted (selective — traces are huge)
- **Network:** request count, total wire bytes, third-party count, failed (4xx/5xx); **top-N slowest** (by total
  time / `wait` = server latency); **top-N largest payloads** (`content.size`); **uncompressed text assets**
  (script/stylesheet/document/fetch with no `content-encoding`); **third-party weight** (origins by count+bytes);
  **cacheable assets** (missing/short `cache-control`). *(Refinement vs the prototype: only flag text-type assets
  as "uncompressed" — a 2 MB image is "large image," not "uncompressed.")*
- **Console — site errors only.** ★ Two-stage filter, both proven:
  1. **Level:** keep `error`/`warning`, drop `info`/`log` (dropped **72** LaunchDarkly/SignalR/Meta-Pixel chatter).
  2. **Extension denylist:** drop `grammarly`, `recorder.contentScripts`, `message port closed`, `DEFAULT root
     logger`, `AAA-init`, `chrome-extension://`, `moz-extension://` by text **or** `location.url`. Proven: 5/5
     synthetic extension lines dropped, 2/2 real site errors kept. *(This prod trace was headless → 0 extension
     noise, but the filter is the load-bearing correctness piece and is ready + tested.)*
  3. **Origin tag:** mark each kept message `site` (host == target / subdomain) vs `3p`, so the model can say
     "the site's error" vs "a third-party script's error" honestly.
- **Timing / Web Vitals:** per-action durations + page-load timing, **best-effort**. ★ Honesty caveat below.

### Cost + size
Send the *summary* JSON (top-N lists + filtered console), not the trace. Bounded input (~hundreds of tokens) →
predictable per-click cost. Gate the endpoint (a write/spend, see §5) so only editors/admins can spend tokens.

---

## 3. The AOAI call (mirror the proven path, in C#)

`POST .../openai/deployments/gpt-5-mini/chat/completions?api-version=2025-04-01-preview`, MI bearer token
(scope `https://cognitiveservices.azure.com/.default` via the API's `DefaultAzureCredential`), JSON mode.

**Prompt → structured, categorized, severity-ranked output:**
```json
{
  "summary": "one or two plain sentences",
  "insights": [
    { "category": "performance|network|errors|suggestions",
      "title": "short actionable headline",
      "detail": "what + why, grounded in the trace data provided",
      "evidence": "the specific request/console line it's based on",
      "severity": "high|medium|low",
      "confidence": "high|medium|low" }
  ],
  "caveats": ["e.g. Web Vitals are best-effort for this SPA"]
}
```
System prompt instructs: *only* reason from the provided summary; **don't fabricate a Lighthouse score** (frame
as "suggestions based on the trace's network/console data," not a real audit); separate the site's errors from
third-party; say "couldn't determine" rather than invent. Same honesty discipline as RCA.

---

## 4. UX

A **"Get AI insights"** button on the run-detail / trace view (next to the existing trace viewer affordance).
Click → loading state (analysis takes a few seconds: download blob + parse + AOAI) → render **categorized cards**:
**Performance · Network · Errors · Suggestions**, each insight severity-tagged, with its evidence line and a
confidence label. Show the caveats (SPA Web Vitals) inline. On AOAI/extraction failure → a graceful "couldn't
analyze this trace" (never a hard error), mirroring RCA's non-fatal stance.

---

## 5. Where it lives + the build slices (Craig decides)

**Recommended home: the C# API**, on-demand. The API already (a) has the trace bytes + MI to read the blob, and
(b) is the on-demand HTTP service the button needs (the runner is a scheduled job, not a service — an interactive
click shouldn't enqueue an ACA job + poll). So: `POST /api/runs/{id}/ai-insights` → download blob → parse →
summarize (filtered) → AOAI → return. **Gate it** (slice-2 auth): it spends money + calls AOAI, so editor/admin
only — natural cost control. Async UI (loading state).

> ★ **Deploy prerequisite (a manual role assignment, like the ACS email one):** the API's system-assigned MI
> needs **`Cognitive Services OpenAI User`** on `synthwatch-aoai`, plus app settings `AZURE_OPENAI_ENDPOINT`,
> `AZURE_OPENAI_DEPLOYMENT=gpt-5-mini`, `AZURE_OPENAI_API_VERSION=2025-04-01-preview`. Until then the endpoint
> returns a graceful "AI not configured."

**Proposed slices (each independently shippable):**
1. **Trace extraction + signals endpoint** (API, one clean PR): `TraceSignals` parser (System.IO.Compression +
   NDJSON) → compact summary; `GET /api/runs/{id}/trace-signals` returns it. **No AOAI, no deploy prereq.**
   Independently useful (the dashboard could show the waterfall / filtered console with no AI). Fully testable
   against a small committed fixture trace + the extension-noise filter test. *This is the clean first PR.*
2. **AOAI insights** (API): a C# AOAI client mirroring `runner/aoai.ts` + the prompt + `POST /api/runs/{id}/ai-insights`
   (gated). Carries the MI-role deploy prereq.
3. **Dashboard** (dashboard repo): the "Get AI insights" button + categorized cards + client + loading state.

---

## 6. Honesty caveats (must surface in the output)
- **SPA Web Vitals are unreliable.** Wegmans is a Next.js SPA; LCP/CLS/INP for soft navigations are partial in
  the trace. **Lean on network + console + payload sizes** (solid); present Web Vitals as *best-effort*, never
  authoritative.
- **Not a real Lighthouse audit.** We don't run Lighthouse here — frame suggestions as "based on the trace's
  network/console data." (Actually running Lighthouse on demand is a *much* bigger lift — note as a future option.)
- **Site vs third-party vs unknown.** Distinguish the site's own errors (its `_next` chunks / origin) from
  embedded third-party scripts (LaunchDarkly, ad/tracking, chatbots) — and say "couldn't determine" over guessing.

---

## Sample insight (what slice 2 would produce from run 844486's summary)
> **[Performance · medium]** Main-thread JS chunks are slow to first byte. Several `_next/static/chunks/*.js`
> requests show ~700 ms server `wait` before any bytes — render-blocking the SPA boot. *Evidence: `05qfr1~43mehr.js`
> 1026 ms (wait 613 ms).* Consider edge-caching/preloading the critical chunks.
> **[Network · medium]** `images.wegmans.com` dominates page weight — 70 requests / 6.6 MB. *Evidence: a single
> hero image is 2.2 MB.* Serve responsive/next-gen formats and right-size hero images.
> **[Errors · high, site]** The search header logs `Invalid discovery pages storage data` on load — relevant to a
> *search* monitor. *Evidence: console error from `_next/static/chunks`.* Worth a look: corrupt/edge-case
> localStorage state in `SiteHeaderSearch`.
> **[Suggestions · low]** Two CSS chunks are `preloaded but not used within a few seconds` — wasted preloads.
> *Caveat: Web Vitals omitted — unreliable for this SPA's soft navigation.*
