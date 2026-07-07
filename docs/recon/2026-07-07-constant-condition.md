# Recon — the 2 `constant-condition` alerts on the reconcile-apply guard

**Scope:** analysis only. Adjudicate to ground truth whether alerts **#151** and **#152**
(`cs/constant-condition`, `Functions/ReconcileFunctions.cs`) are genuine false-positives (safe to
dismiss) or mask a real always-true/false condition that weakens the destructive-SQL guard. **No fix,
no dismissal** — recommendation only.

**Verdict (both):** ✅ **FALSE POSITIVE — safe to dismiss. NOT a real bug; the destructive-SQL guard is
intact.** The flagged expression is a deliberate, *load-bearing* null-flow guard that the C# compiler
needs and that CodeQL's deeper dataflow correctly proves logically redundant. Evidence + falsifiers below.

---

## STEP 1 — the 2 alerts (OBSERVED)

`gh api code-scanning/alerts/{151,152}`:

| Alert | Rule | Msg | Scanned-commit loc | Current origin/main loc |
|---|---|---|---|---|
| **#151** | `cs/constant-condition` | "Condition is always false because of … is …." | `ReconcileFunctions.cs:295` cols 70–88 | `:300` |
| **#152** | `cs/constant-condition` | "Condition is always false because of … is …." | `ReconcileFunctions.cs:327` cols 72–90 | `:332` |

The last CodeQL scan ran on commit `3cafa16`; the code has since shifted (+5 lines) after #180–#185
merged — the alert line numbers are stale, the *content* is the anchor.

**The exact flagged sub-expression** (extracted from `3cafa16` at the reported columns) is, in BOTH
alerts, literally:

```
normalized is null
```

Not the `stmt!.Values is not { Count: … }` pattern — the analyzer flags the `normalized is null` clause.

---

## STEP 2 — the guard code + what it protects (OBSERVED, origin/main)

Both sites are reconcile-apply shape guards that execute a runner-emitted `UPDATE` **verbatim** on a
transaction, so a malformed/destructive statement reaching here would run against prod. Identical
flagged shape in each:

`ApplyMissingAsync` (#151, `:296-304`) — soft-disable a removed check, NEVER delete it:
```csharp
var stmt = doc?.Statements?.FirstOrDefault();
var text = stmt?.Text;
var normalized = text is null ? null : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()); // :298
if (text is null || stmt!.Values is not { Count: 1 } vals || normalized is null                       // :300  ← flagged
    || !normalized.Contains("UPDATEchecksSETenabled=falseWHEREsource_key=$1", …)                       // :301
    || normalized.Contains("DELETE", …))                                                               // :302
    throw new InvalidOperationException("missing plan is not the expected single soft-disable statement…");
```

`ApplyChangedAsync` (#152, `:327-339`) — reconverge non-redaction fields, redaction-excluded, never delete:
```csharp
var normalized = text is null ? null : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()); // :329
if (text is null || stmt!.Values is not { Count: > 0 } vals || normalized is null                     // :332  ← flagged
    || !normalized.StartsWith("UPDATEchecksSET", …) || !normalized.Contains("WHEREsource_key=$1", …)
    || normalized.Contains("sensitive", …) || normalized.Contains("redact_patterns", …)
    || normalized.Contains("DELETE", …))                                                               // :337
    throw new InvalidOperationException("changed plan is not the expected scoped, redaction-excluded UPDATE…");
```

**What CodeQL claims + why it's right (OBSERVED):** `normalized = text is null ? null : new string(char[])`.
`new string(char[])` never returns null (empty input → `""`). Therefore `normalized is null ⟺ text is
null`. In the OR chain, `text is null` is the FIRST clause and short-circuits; `normalized is null` is
only reached when `text` is non-null, at which point `normalized` is the `new string(...)` result —
non-null. So `normalized is null` is **always false at its position**. CodeQL's dataflow is *correct*.

---

## STEP 3 — is it accidental (real bug) or intentional (false positive)? Falsifiers run

### Falsifier A — "Can `text` be non-null while `normalized` is null?" (would make it a live, varying guard)
**Ran:** inspected the assignment. `new string(text.Where(...).ToArray())` — the `string(char[])`
ctor returns `""` for an empty array, never `null`. **No path** yields `text != null && normalized ==
null`. → the condition genuinely **cannot vary**; it is always-false by construction. *(Refutes "it's a
live guard".)*

### Falsifier B — "If the always-false clause were gone, could a destructive/malformed plan slip through?" (the security question)
**Ran (reasoning over the OR-of-refusals):** the guard THROWS if any clause is true. `normalized is
null` being always-false means it never *contributes* a refusal — but every other refusal clause still
executes: `Contains("DELETE")`, the required-UPDATE-shape checks (`Contains("UPDATE…SET…WHERE
source_key=$1")` / `StartsWith("UPDATEchecksSET")` + `Contains("WHEREsource_key=$1")`), and the
redaction-column bans. The *only* input `normalized is null` could uniquely catch is a null
`normalized` — which arises **only when `text is null`**, already refused by the first clause. →
**nothing slips through.** The destructive-SQL prevention is enforced entirely by the live sibling
clauses. *(Refutes "it weakens the guard".)*

### Falsifier C — "Why is the clause there at all? Is its always-false-ness an accidental typo?" (empirical)
**Ran (throwaway experiment, reverted, not committed):** deleted `|| normalized is null` from both
guards and built `dotnet build -warnaserror`:
```
ReconcileFunctions.cs(301,17): error CS8602: Dereference of a possibly null reference.
ReconcileFunctions.cs(333,17): error CS8602: Dereference of a possibly null reference.
    2 Error(s)
```
→ The clause is a **deliberate, load-bearing null-flow guard**: the C# compiler's nullable analysis
(shallower than CodeQL's — it does not correlate `text is null` with `normalized`) requires
`normalized is null ||` to narrow `normalized` to non-null before the `.Contains()`/`.StartsWith()`
dereferences. Remove it → the `-warnaserror` build fails. It is NOT a typo and NOT a mis-inverted shape
check; the intended shape/DELETE checks are all present, correct, and reachable. *(Refutes "accidental
constant / real bug".)*

---

## Verdict + recommendation (Craig adjudicates — nothing dismissed here)

**#151 and #152 — FALSE POSITIVE, safe to dismiss (`false positive`).** Ground truth, not hypothesis:
the flagged `normalized is null` is always-false *by construction* (`normalized is null ⟺ text is
null`, and `text is null` short-circuits first), it is a **compiler-required null-flow guard** (proven
by the CS8602 build failure when removed), and it does **not** weaken the destructive-SQL prevention —
DELETE-refusal and the required-UPDATE-shape checks are separate, live clauses. This is precisely the
"intentional assertion the deeper analyzer proves redundant" false-positive shape, on a guard where
the security property holds independently of the flagged clause.

**Do NOT "fix" by deleting the clause** — that reintroduces the CS8602 build break; any real change
(e.g. `normalized!` or restructuring) is churn on a security-critical guard for zero behavior gain.
Dismissal is the correct disposition.

**Proposed dismissal (Craig runs, or not) — `dismissed_comment` = 279 chars, within the 280 cap. No
backticks in the comment (they would trigger bash command-substitution):**
```bash
for n in 151 152; do
  gh api -X PATCH "repos/craigoley/synthwatch-api/code-scanning/alerts/$n" \
    -f state=dismissed -f dismissed_reason="false positive" \
    -f dismissed_comment="normalized is null is always-false by construction (normalized null iff text null; text-null short-circuits first). Its a compiler null-flow guard (removing it -> CS8602). Destructive-SQL refusal is enforced by the live DELETE / required-UPDATE-shape clauses; guard not weakened."
done
```
