# Code-scanning backlog + open-issues triage — 2026-07-07

**Scope:** analysis + the clearly-fixable subset. Branch off `origin/main`. `security_events` scope
present (STEP 0: `code-scanning/alerts?state=open` → 200). **52 open alerts** (all CodeQL
code-quality, no security-severity, + 2 Semgrep config) and **3 open issues**.

**Evidence contract:** every finding cites the alert #, issue #, or `file:line`. OBSERVED (source /
command output) vs INFERRED separated. Each alert is **TRUE POSITIVE** (fix) or **candidate FALSE
POSITIVE / won't-fix** (propose dismissal with evidence — never dismissed here). Discipline: when in
doubt, FIX not dismiss; if not cheaply fixable, propose-with-evidence.

## Summary

| Bucket | Count | Action |
|---|---|---|
| Fixed (TRUE POSITIVE) | 6 alerts | PRs #183, #184 (auto-resolve on next scan) |
| Propose dismiss — false positive | 40 | STEP 4 §A |
| Propose dismiss — won't-fix (intentional) | 4 | STEP 4 §B |
| Propose — needs decision (security-adjacent) | 2 | STEP 4 §C |
| Issues resolved + closed | 3 | STEP 2 |

**Nothing was dismissed or PATCHed.** Fixes let the scan close alerts; proposals are Craig's call.

---

## STEP 1 — code-scanning triage (all 52)

### FIXED — true positives (→ PRs)

**Semgrep `dependabot-missing-cooldown` ×2 — #108, #109 → PR #183.** OBSERVED: `.github/dependabot.yml`
had no `cooldown` on either `package-ecosystem`. TRUE POSITIVE (supply-chain hygiene). Fix: added
`cooldown: { default-days: 7 }` to nuget + github-actions.

**`cs/useless-upcast` — #149 → PR #184.** `StatusPageProjection.cs:43` `(Pct: (decimal?)null, Building:
true)`. TRUE POSITIVE — **verified**: removing the cast builds 0/0 (the ternary target-types the tuple
off `uv`). ⚠️ The sibling cast on `:32` (`(decimal?)null` in a `null`-vs-`decimal` ternary) IS
load-bearing and was left intact — only `:43` was flagged/fixed.

**`cs/xmldoc/missing-summary` — #105 → PR #184.** `ProblemResults.cs:16` `Body()` had `<param>` docs but
no `<summary>`. TRUE POSITIVE. Fix: added the summary.

**`cs/linq/missed-where` ×2 — #103, #104 → PR #184.** OBSERVED: `TagsFunctions.cs:62` (`if (n is not
null)`) and `SensitiveRedaction.cs:27` (`if (IsNullOrWhiteSpace) continue`) are implicit foreach
filters. TRUE POSITIVE (quality). Fixes are behavior-preserving `.Where(...)`; #104 (security file) is
pinned by a new `SensitiveRedactionTests` case (blank patterns skipped, real patterns still redact).

### PROPOSE dismiss — FALSE POSITIVE

**`cs/call-to-object-tostring` ×40 — #110-147, #150, #153.** OBSERVED: every flagged line sits inside a
`FromSql($@"… {tags} …")` / `ExecuteSqlInterpolated` interpolated **FormattableString** (e.g.
`ReportsFunctions.cs:194` `ANY({tags})`, `:516` `cardinality({tags})`, `ReconcileFunctions.cs:195`
`ANY({executable})`). The message flags a `[String\]` (`System.String[]`) interpolant — the `{tags}` /
`{executable}` arrays. INFERRED (false positive): EF `FromSql(FormattableString)` extracts the
interpolation arguments via `FormattableString.GetArguments()` and binds them as **DB parameters** —
Npgsql sends a `string[]` as a `text[]` parameter. **No runtime `ToString()` happens** and the query is
parameterized (not string-concatenated). This is the CLAUDE.md-documented FP class
(*"`ExecuteSqlInterpolatedAsync($"…{x}…")` binds args as parameters … CodeQL 'ToString on
FormattableString' / SQL-injection flags on those are false positives"*). **No safe fix exists** —
`ToString()`-ing or de-interpolating would break parameterization/security. → propose dismiss (false
positive). Falsifier: *"the interpolant is stringified into the SQL text (real injection/ToString)."*
Refuted — `FromSql` takes `FormattableString`, args are bound, not formatted.

### PROPOSE dismiss — WON'T-FIX (intentional, handled)

**`cs/catch-of-all-exceptions` ×3 — #106, #107, #154.** All are deliberate never-fail guards that DO
handle the exception:
- **#106 `AuditWriter.cs:53`** — `catch (Exception ex) { onFailure?.Invoke(ex); return false; }` — the
  audit-write never-throws guard (an audit failure must not break the response; `onFailure` logs).
- **#154 `AuthorizationMiddleware.cs:133`** — `catch (Exception ex) { AuthzLog.AuditFailed(_logger, ex); }`
  — same never-throw audit contract, logs.
- **#107 `ReconcileFunctions.cs:273`** — bare `catch { await tx.RollbackAsync(ct); failed.Add(...); }` in
  the per-plan apply loop: broad by design so ONE plan's failure rolls back + is recorded without
  aborting the batch (*"never half-apply"*). Correct broad catch.
→ propose dismiss (won't fix — intentional, exception is handled, not swallowed silently).

### PROPOSE — NEEDS DECISION (do NOT dismiss blind — security-adjacent)

**`cs/constant-condition` ×2 — #151, #152.** `ReconcileFunctions.cs:295` (`stmt!.Values is not { Count:
1 } vals`) and `:327` (`is not { Count: > 0 } vals`), inside the **reconcile-apply shape guards**
(`ApplyMissingAsync` / `ApplyChangedAsync`) that refuse any plan that isn't a scoped
`UPDATE … WHERE source_key = $1` / is a `DELETE`. INFERRED: likely a CodeQL misread of the
property-pattern-with-binding in a negated pattern (a known noise pattern) — the guard is **defense-in-
depth** and the `vals` binding is used (`vals[0].GetString()`), so it is not dead; even if this one
clause were constant, the sibling clauses (`!Contains("UPDATE…")`, `Contains("DELETE")`) still enforce
the security property. BUT these guard destructive-SQL prevention, so **do not dismiss on the assumption
of FP.** Recommendation: Craig (or a follow-up) confirms the pattern semantics; if confirmed FP →
dismiss (false positive), else a careful rewrite. Left OPEN.

### PROPOSE dismiss — WON'T-FIX (subjective metric)

**`cs/complex-condition` ×1 — #148.** `StatusPageProjection.cs:73` `IsDownCritical`: `(c.Severity ==
"critical" && (c.Status == "fail" || c.Status == "error")) || (c.HasOpenIncident && c.OpenSeverity ==
"critical")` — two clear, commented clauses. INFERRED: refactoring an outage-classification predicate
purely to satisfy a logical-operator-count metric adds indirection and risks logic drift for no
correctness gain. → propose dismiss (won't fix). Craig's call if he prefers a refactor.

---

## STEP 2 — the 3 open issues (all classification **(c) already-done/stale** → closed)

Each resolving PR merged to `origin/main` but did not auto-close its issue. Verified against
`origin/main` before closing:

- **#172** — bicep `AUTH_ENFORCEMENT_ENABLED` defaults False. **RESOLVED by #173** (`6854465`):
  `infra/main.bicep:56` = `param authEnforcementEnabled bool = true`. → **closed**.
- **#169** — `failure_threshold` default divergence. **RESOLVED by #176** (`d751ad6`): `Check.cs:38` `= 1`,
  `CheckValidation.cs:112` `?? 1`, + pinning test `New_check_without_explicit_failure_threshold_defaults_to_1`.
  → **closed**.
- **#158** (`bug`) — parse-intent drops method/headers/body/assertions. **RESOLVED by #165** (`1e3efa1`):
  `ParseIntent.cs:91-97` (Suggestion DTO carries the fields) + `ParseIntentFunctions.cs:64-70` (maps them
  into `CreateCheckRequest`) — both layers of the original two-layer drop fixed. → **closed**.

---

## STEP 3 — executed

- **PR #183** — `chore(dependabot): 7-day cooldown` → auto-resolves **#108, #109**.
- **PR #184** — `chore(codeql): 4 code-quality true-positives` → auto-resolves **#103, #104, #105, #149**.
- **Issues closed:** #172, #169, #158 (with evidence comments citing the merged resolving PRs).

Fixes reference the alert numbers so the next scan closes them; no alert was PATCHed to dismissed.

---

## STEP 4 — proposals for Craig (evidence above; run or not)

### §A — false positives (40) — `cs/call-to-object-tostring` (FromSql FormattableString binds as parameter)

```bash
for n in 110 111 112 113 114 115 116 117 118 119 120 121 122 123 124 125 126 127 128 129 \
         130 131 132 133 134 135 136 137 138 139 140 141 142 143 144 145 146 147 150 153; do
  gh api -X PATCH "repos/craigoley/synthwatch-api/code-scanning/alerts/$n" \
    -f state=dismissed -f dismissed_reason="false positive" \
    -f dismissed_comment="FromSql/ExecuteSqlInterpolated FormattableString — interpolants bound as DB params (GetArguments), never ToString()'d. Parameterized, not concatenated."
done
```

### §B — won't-fix (3) — `cs/catch-of-all-exceptions` (intentional never-throw / rollback guards)

```bash
for n in 106 107 154; do
  gh api -X PATCH "repos/craigoley/synthwatch-api/code-scanning/alerts/$n" \
    -f state=dismissed -f dismissed_reason="won't fix" \
    -f dismissed_comment="Intentional never-fail guard: exception is handled (audit onFailure/log, or per-plan rollback+record), not silently swallowed."
done
```

### §B2 — won't-fix (1) — `cs/complex-condition` #148 (clear predicate; refactor risks logic drift)

```bash
gh api -X PATCH "repos/craigoley/synthwatch-api/code-scanning/alerts/148" \
  -f state=dismissed -f dismissed_reason="won't fix" \
  -f dismissed_comment="IsDownCritical is two clear commented clauses; refactoring an outage predicate for a complexity metric risks logic drift for no correctness gain."
```

### §C — needs decision (2) — `cs/constant-condition` #151, #152 (reconcile-apply security guards)

**Leave OPEN.** Confirm the negated-property-pattern semantics of `stmt!.Values is not { Count: 1 }
vals` first. If confirmed a CodeQL misread → dismiss `false positive`; otherwise a careful guard rewrite.
Do not dismiss on assumption — these gate destructive-SQL prevention.

> Reminder (dismiss `dismissed_comment` is capped at 280 chars — HTTP 422 otherwise; the comments above
> are within budget).
