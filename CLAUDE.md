# synthwatch-api — Claude rules

Rules Claude should follow when working in this repo.

## Auth gate decision rule (copy this when adding an endpoint)

**Mutating verb (POST/PUT/PATCH/DELETE)?** Do nothing — the middleware verb-gate covers it automatically
(fail-closed: editor/admin session required; `/editors*`/`/access-requests*` are admin-floor in the gate;
`/auth/logout` is session-floor). **GET that serves credentials, forensic artifacts, or operator config?**
Add a handler self-guard with the `RequireSessionAsync` pattern (#154) — the verb-gate never sees reads.
**User management or anything that must hold even mid-rollout?** Make the handler guard ignore the flag
(the Editors pattern). `AUTH_ENFORCEMENT_ENABLED` exists for **deploy safety only**: it is FAIL-CLOSED
(ON unless explicitly `false`/`0`), turning it off opens every write, all paid-AOAI endpoints, and
reconcile/apply at once, and startup logs a warning while it is off — never design an endpoint's security
around the flag being off.

## Lessons from 2026-06-29

- **The runner (synthwatch repo) OWNS the DB schema + migrations; this API serves reads/writes against it.** API entity/DbContext changes must match columns the runner's migrations create, and any validation gate should MIRROR the runner's canonical rule (`reconcile.ts`, `tags.ts`, `locations.ts`) exactly — as a shared pure helper with a cross-ref comment + predicate-parity `[InlineData]` tests so the two stay in sync. *(from #114 — `CheckValidation.SensitiveNeedsRedaction` mirrors `reconcile.ts:181`; same pattern as the setCheckTags/setCheckLocations mirrors and the "cross-repo contract fix" commits)*

- **Merged ≠ migrated.** This API auto-deploys on merge. Before merging API code that reads a NEW column, verify the runner's migration is actually APPLIED in the target env (`SELECT max(version) FROM schema_migrations`, or confirm the column exists) — not merely merged in the runner repo — or the deploy crashes querying a non-existent column. *(from #114 — runner #137/migration 0046 was merged to runner main while prod was still at 0045 mid-task; the read-only mapping was only deploy-safe once 0046 was applied)*

- **CodeQL `paths-ignore` is a silent no-op for compiled C#.** The manual `dotnet build` compiles `obj/.../*.g.cs` (Functions Worker SDK + regex source generator), so the extractor flags them regardless of the globs; `query-filters` can't scope by path either. The working exclusion is a post-analysis `advanced-security/filter-sarif` step (filters by result location). *(from #113)*

- **CodeQL triage discipline:** dismiss only with a cited reason from {proven false-positive, won't-fix (tests/generated), mitigated-elsewhere}; never dismiss a security-category finding unless proven FP; split a rule group if only some instances are real (generated-file alerts carry `classifications:["generated"]`). The GitHub code-scanning `dismissed_comment` is **capped at 280 chars** (HTTP 422 otherwise) — keep justifications terse. Note: `ExecuteSqlInterpolatedAsync($"…{x}…")` binds args as parameters (FormattableString), so CodeQL "ToString on FormattableString" / SQL-injection flags on those are false positives. *(from #111/#112/#113)*

- **ASP.NET Functions route params arrive ALREADY URL-decoded** (`ConfigureFunctionsWebApplication` decodes before binding) — don't add `Uri.UnescapeDataString` (double-unescape corrupts URL-encoded emails). For set-based deletes use `ExecuteDeleteAsync`, not load-then-`RemoveRange`-then-`SaveChanges` (the tracker/audit path 500'd). *(from the #104→#105→#106→#107 access-requests delete chain — #104 guessed "missing decode" and was the wrong fix; #106 found the real cause)*

- **ARM/ACA job-start POSTs (`Microsoft.App/jobs/start`) require `Content-Type: application/json` even with an empty body** — a `text/plain` empty body returns 415 and the job never fires. *(from #101 — the on-demand "Run now"/reconcile trigger silently no-op'd until this)*

- **The reconcile/run-now triggers fire AS the API's system-assigned managed identity** (`DefaultAzureCredential` in `Program.cs`, not admin/passed creds). To PROVE a MI-gated action, hit the DEPLOYED endpoint authed so the start runs under the function's MI — running `az` as admin proves nothing about the MI. The MI needs `Container Apps Jobs Operator` (covers `Microsoft.App/jobs/start/action`) on the job; codify the grant in `infra/main.bicep`, don't leave it as a manual CLI assignment. *(from #108/#115)*

- **To authenticate as admin for a live proof, mint a `sessions` row directly:** `token_hash` = lowercase sha256-hex of an opaque token, `email` = an `ADMIN_EMAILS` entry, set `expires_at`, then send `Authorization: Bearer <token>`; delete the row afterward. *(from #115 reconcile-trigger proof)*

- **"Stale rows on a 200" from the runs/incidents list endpoints is usually NOT server caching.** These endpoints window by `started_at < to` where `to` defaults to request-time `now`; there is no response cache (scoped DbContext + `AsNoTracking`), so identical real round-trips returning a frozen newest-id is typically the CLIENT freezing the `to` param. Localize the layer with the psql-vs-curl test: run the endpoint's exact query in psql and curl the live endpoint at the same instant. *(from the runs-endpoint-stale-rows recon — the "API is stale" premise was actually the dashboard's `useDateRange` freezing `to` at mount)*

- **Build/verify gate:** `dotnet build -warnaserror` (expect 0/0) + the Testcontainers xUnit suite (needs Docker running). Recon-first earns its keep here — multiple tasks' stated root cause was wrong (B10 "already landed" wasn't; "the API returns stale rows" was a client bug), so verify a task's asserted layer from code + a live test BEFORE building the fix. *(from #114 and the B10 / reconcile / runs-stale recon tasks)*

## Lessons from 2026-07-02

- **`runs.retry_count` is an ATTEMPT count (runner migration 0048): `1` = first try / NO retry; an ACTUAL retry is `retry_count > 1`, and `0` never occurs in real data.** Any "was this run retried?" logic must filter `retry_count > 1`, never `> 0` (which counts every clean pass as retried). *(from #152 — #149's `/trust` SQL used `> 0`, so every healthy monitor showed "flaky"; the fix flipped it to `> 1`. ★ Worse: the test seeded `retry_count=1` to mean "retried" — the SAME inverted assumption as the code — so the suite was 328/328 green while prod was wrong. Ground a fixture in what the value MEANS in prod, not in the code's reading of it.)*

- **`HttpRequest.ReadFromJsonAsync<T>()` throws `System.InvalidOperationException` (NOT the `JsonException` handlers catch) on a wrong/absent `Content-Type`** — so a `catch (JsonException)` misses it and it becomes a shielded 500. Read JSON bodies through the shared `Infrastructure/RequestJson.ReadAsync<T>` helper: it guards `HasJsonContentType()` → 415 first, then catches `JsonException` → 400. Don't hand-roll a bare `ReadFromJsonAsync` + `catch (JsonException)`. *(from #155 — every body endpoint 500'd on `Content-Type: text/plain`.)*

- **The AuthorizationMiddleware verb-gate returns `Allow` for EVERY GET (reads are open by default) and only sets `http.Items["principal"]` for audited MUTATIONS** — so a GET that must be authenticated (the forensic-artifact endpoints) CANNOT lean on the middleware; it must self-guard by resolving `IAuthPrincipal.FromBearerAsync` and requiring `principal.CanWrite` (editor/admin), gated on `AuthorizationMiddleware.EnforcementEnabled()`. Require the role FLOOR, not just `principal != null` — a revoked editor's still-valid session resolves to `anonymous` and would pass a null-check. *(from #154 — the four artifact endpoints leaked forensic data anonymously; review caught the any-session-vs-editor/admin gap → added the `CanWrite` floor.)*

- **The Testcontainers fixture `tests/SynthWatch.Api.Tests/fixtures/schema.sql` is a hand-maintained snapshot that LAGS live prod** (the inverse of "merged ≠ migrated" — here the TEST env trails). It has been missing columns/enum values that exist in prod: `runs.spec_provenance`, and `infra_error` in the `runs_status_check` CHECK. Before writing a test that needs a column or CHECK value, confirm the fixture has it and add it to mirror prod, or the seed SQL fails. *(from #153 — added `infra_error` to seed an errored run; the trust work similarly had to add `spec_provenance`.)*

- **A mutating POST/PUT/DELETE 401s at the AuthorizationMiddleware BEFORE the handler runs when `AUTH_ENFORCEMENT_ENABLED` is on.** To reproduce a HANDLER-level bug against the deployed API, hit an unauth-allowlisted endpoint (`/auth/request-code|verify|request-access`) or send a valid bearer — an anonymous curl to a gated mutating route proves nothing about the handler and returns a misleading 401. *(from #155 — the task's suggested repro `POST /checks/parse-intent` returned 401 for every content type and never reached the body-parse bug; the unauth `POST /auth/request-code` reproduced the 500.)*

- **Host matching against `deploys.target_host` needs query-side normalization** — lowercase + strip a leading `www.` on BOTH sides (`regexp_replace(lower(host),'^www\.','')`). The runner's `hostOf()` stores `www.`, so an apex-host check (e.g. the `wegmans.com` SSL check) can never string-match a `www.wegmans.com` deploy. Normalize READ-side only; the runner's stored convention + its `(target_host, fingerprint)` dedup key are runner-owned and out of scope. *(from #157 — the incident deploy-proximity match.)*
