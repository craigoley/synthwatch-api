# Recon — the API write-path for `environment` / `rewrite_from_origin` (write-gate option c)

**Question:** the write-gate recon is deciding HOW `environment` + `rewrite_from_origin` get written.
One candidate (option c) is an **API write-path** (a PATCH on the API). This scopes that option to
build-ready — or rules it out. It does NOT prejudge the runner recon (which picks the mechanism).

**VERDICT (ground truth):** ❌ **DEAD-ON-ARRIVAL for the checks pre-prod-arc actually targets**
(Git-managed manifest monitors). Both columns are **GIT-AUTHORITATIVE** in the runner's reconcile — the
*manifest* owns them — so an API write drifts and gets clobbered back to the manifest value. The
API-field write survives ONLY on hand-made (`source_key IS NULL`) checks, which are not the pre-prod-arc
target. The canonical write-path is the **manifest** (declare `environment:` in synthwatch-monitors'
`manifest.json`; reconcile syncs it — already implemented).

---

## Q1 — is there a write surface on checks today? YES (pattern to mirror)

**OBSERVED — the API is NOT read-only for checks.** `Functions/ChecksFunctions.cs`:
- `POST /checks` (`:209`), `PATCH /checks/{id}` (`:270` — *"partial edit / pause"*), `DELETE /checks/{id}` (`:369`).
- Plus `PUT /checks/{id}/locations`, `/tags`, and the reconcile mutation endpoints.

**The PATCH pattern (`UpdateCheck`, `:268-297`):** `RequestJson.ReadAsync<UpdateCheckRequest>` (415/400
guarded) → load check → `CheckValidation.ApplyPatch(body, check)` (field-keyed error dict) →
`SaveChangesAsync` → return `CheckDetailDto`. **A net-new env-write is NOT net-new plumbing** — it's two
fields added to `UpdateCheckRequest` + `ApplyPatch`. So option c is *cheap to build*; the blocker is not
cost, it's Q2.

**INFERRED — auth (see Q4):** the handler is `AuthorizationLevel.Anonymous` at the Functions layer, but a
mutating verb rides the `AuthorizationMiddleware` verb-gate → editor/admin session required. No new auth
model.

## Q2 — ★ THE SURVIVE-RECONCILE QUESTION (the crux) → the API write does NOT survive

**OBSERVED — the two columns are Git-authoritative, declared so on purpose (runner `reconcile.ts`,
origin/main):**

```ts
// reconcile.ts:344-350 — "Pre-prod-arc S3 (0059/0060): the manifest owns environment + rewrite_from_origin."
const gitEnv = m.environment ?? 'prod';
if (existing.environment !== gitEnv) diff.environment = { git: gitEnv, live: existing.environment };
const gitRewrite = m.rewrite_from_origin ?? null;
if ((existing.rewrite_from_origin ?? null) !== gitRewrite) diff.rewrite_from_origin = { ... };
```
```ts
// reconcile.ts:388-403 — GIT_AUTHORITATIVE_COLUMNS: overwritten on every apply (INSERT and UPDATE)
export const GIT_AUTHORITATIVE_COLUMNS = [
  'name','kind','target_url','flow_name','sensitive','redact_patterns',
  'environment',            // "Non-redaction, so they auto-join CHANGED_UPDATE_COLUMNS
  'rewrite_from_origin',    //  (a manifest change re-syncs them)."
] as const;
```

**INFERRED — the clobber:** if the API writes `environment='staging'` to a Git-managed check whose
manifest omits `environment` (→ `gitEnv='prod'`), the next reconcile computes
`existing.environment ('staging') !== gitEnv ('prod')` → a `changed` drift row → the apply UPDATE
(built from `CHANGED_UPDATE_COLUMNS`, which includes `environment`) writes it back to `'prod'`. **The API
write is overwritten.** This is not a bug to fix — it is the *designed* source-of-truth split
(`reconcile.ts:12-17`: "Git and the dashboard own DISJOINT fields"). `environment`/`rewrite_from_origin`
are on the **Git** side.

**Two nuances that narrow, but do not save, option c:**

1. **Scope: manifest-managed checks only.** `reconcile.ts:52` — reconcile reads *"the Git-managed `checks`
   … `source_key IS NOT NULL` rows."* A **hand-made check (`source_key IS NULL`) is never reconverged** →
   an API write to its `environment` **survives**. *(Falsifier for "totally dead": this hand-made slice.)*
   BUT pre-prod-arc is about *manifest* monitors declaring their environment (`reconcile.ts:344`), and a
   hand-made check defaults `environment='prod'` and can already be edited via the existing PATCH — so
   the API-field option only "works" for the checks pre-prod-arc does **not** care about.
2. **Timing: the clobber is currently pending, not yet live — but the drift is immediate.** `reconcileMain
   .ts:5,8` is **detect-only** ("write reconcile_drift … field-split apply upsert is gated off — a later PR
   enables it"), and the API `POST /reconcile/apply` executes **only `drift_type='new'`**
   (`ReconcileFunctions.cs:168,192-195` — *"'changed' is not yet executable by apply"*). So today an API
   env-write is not *auto-clobbered* yet — but it immediately produces a **perpetual `changed` drift** on
   `/reconcile/drift` (manifest 'prod' vs live 'staging'), and it WILL be clobbered the moment the
   changed-apply executor lands (the columns are already in `CHANGED_UPDATE_COLUMNS`). Building an API
   write-path now would be building something the reconcile roadmap is explicitly designed to overwrite.

**One fact that would FLIP the verdict to viable:** move `environment` + `rewrite_from_origin` OUT of
`GIT_AUTHORITATIVE_COLUMNS` (make them dashboard/runtime-owned, like `severity`/`enabled`). That directly
contradicts the committed design (`reconcile.ts:344` "the manifest owns environment") and the schema
comment (`schema.sql:157-161` "the manifest owns it"), so it's a design reversal, not a tweak — and it's
the runner recon's call, not the API's.

## Q3 — IF it were viable: the minimal endpoint (scoped, but only meaningful for the hand-made slice)

Not building a doomed endpoint, but recording the shape so the option is fully costed:

- **Endpoint:** extend the existing `PATCH /checks/{id}` — add `Environment` + `RewriteFromOrigin` to
  `UpdateCheckRequest` and `CheckValidation.ApplyPatch` (mirrors every other patched field). No new route.
- **`environment` validation:** must be in `('prod','staging','dev')` — the DB `CHECK
  (environment IN (...))` (`db/schema.sql:162-164`, migration 0059) is the enforcement; mirror it as a
  pure validator (the runner-mirror pattern in CLAUDE.md) so a bogus value → 400, not a shielded
  23514. Default `'prod'`.
- **`rewrite_from_origin` validation:** a bare `http(s)` **origin** (scheme://host[:port], no
  path/query). The canonical rule is the runner's fail-loud `parseOrigin` (`runner/specfetch/
  hostRewrite.ts:37` — *"FAIL-LOUD: throws on anything that is not a bare http(s) origin"*). Mirror it as
  a shared pure helper with a cross-ref comment (the `reconcile.ts` / `tags.ts` mirror pattern).
- **★ Required guardrail:** the handler MUST refuse (or loudly warn) a write to a `source_key IS NOT NULL`
  (Git-managed) check — otherwise it silently creates the drift Q2 describes. i.e. the endpoint is only
  honest if it restricts itself to hand-made checks. This guardrail is the tell that the API path is the
  wrong tool for the manifest monitors.

## Q4 — auth (a new write surface): no new model needed; one decision to make

**OBSERVED:** `PATCH /checks/{id}` is a mutating verb → gated by `AuthorizationMiddleware`
(editor/admin session, `AUTH_ENFORCEMENT_ENABLED`). There is **no "CI-token" model** in this API — write
auth is bearer-session (editor floor). So an env-write needs **no new auth mechanism**; it inherits the
existing editor/admin verb-gate.

**INFERRED — the one flag (don't hand-wave):** setting `environment='staging'` REMOVES a check from the
prod SLO/mttr/trust fleet (`coalesce(environment,'prod')='prod'` exclude, S1a). That is a
prod-accountability change — arguably **admin-floor**, not editor-floor, since an editor could silently
drop a monitor out of prod scoring. If option c ever ships (hand-made slice), gate it admin-only or audit
it explicitly. This is a decision, not a default.

---

## Net for the write-gate decision

- **Option c (API write-path): ruled OUT for the target** (Git-managed manifest monitors) — DOA against
  the Git-authoritative reconcile split. Viable only for hand-made (`source_key NULL`) checks, which
  pre-prod-arc doesn't target.
- **The write-path that survives is the MANIFEST** (declare `environment:`/`rewrite_from_origin:` in
  synthwatch-monitors' `manifest.json`; reconcile's `changed` sync — `reconcile.ts:344` — carries it to
  the DB). That is presumably option (a)/(b) in the runner recon; this recon says c doesn't compete with
  it for the managed monitors.
- If the runner recon nonetheless wants an API knob for *hand-made* checks, Q3/Q4 above make it a ~1-day
  PATCH extension with the source_key guardrail + admin-floor auth. Otherwise: close option c.
