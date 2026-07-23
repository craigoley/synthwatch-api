#!/usr/bin/env bash
# Schema-parity gate (M3c) — catch the "fixture drifted from the runner schema" class at CI instead of as a
# silent coverage hole (the #129 reconcile_apply_plan miss) or a prod 500. The runner repo OWNS the schema;
# this API's tests/fixtures/schema.sql is a hand-maintained mirror of the objects the API READS — a CURATED
# SUBSET, NOT a full copy of the runner schema. "Faithful" here means faithful to what the API reads, not a
# byte-for-byte replica of runner main. Two classes of runner object are INTENTIONALLY absent and are NOT
# expected to appear (a reader who greps runner main for a column and finds it missing here should look here):
#   • runner-only TABLES the API never maps (e.g. runner_errors) — the DbContext-presence gate only requires
#     MAPPED tables to exist, so an unmapped runner table is legitimately omitted.
#   • a small VETTED ALLOWLIST of runner-internal COLUMNS on shared tables the API never maps — currently
#     checks.baseline_screenshot_url, checks.last_burn_notified_at, incidents.rca_notified_at (see the allowlist
#     in check_runner_column_parity). These pass GREEN BY DESIGN; they are not a gate hole.
# (As of this writing those are the ONLY runner columns absent from the fixture: the 3 allowlisted shared-table
#  columns + the 8 columns of the unmapped runner_errors table — every absence is accounted for, none is a bug.)
#
# ★ IT NEVER DIFFS THE FIXTURE FILE. ~42% of schema.sql is hand-appended blocks with cross-repo comments and
# seed INSERTs (incident memory the CLAUDE.md mirror-pattern depends on); regenerating clobbers them. Instead
# it loads BOTH schemas into live Postgres and diffs the CATALOGS (information_schema.columns +
# pg_get_constraintdef + pg_get_functiondef), so formatting/comments/seed rows are irrelevant.
#
# Modes:
#   (default)     DbContext-presence: every table mapped in Data/SynthWatchDbContext.cs must exist in the
#                 fixture. This is the piece that catches run_requests (mapped, migration 0042, was absent) —
#                 the #129/incident-3 class where a handler compiles green but is integration-untestable. Needs
#                 NO runner checkout, so it runs identically in CI and local dev.
#   --self-test   Prove the catalog-diff engine goes red: load the fixture into BOTH DBs (identical → GREEN),
#                 drop a column from one, assert the engine reports drift + exits non-zero. The must-go-red.
#
# Opt-in (RUNNER_PARITY=1): also diff shared-table COLUMNS/constraints/functions against the CURRENT runner
# schema, in BOTH directions:
#   (b) FIXTURE-BEHIND (check_runner_column_parity) — the fixture must carry every column/constraint the runner
#       defines on a shared table, EXCEPT the small vetted allowlist of runner-internal columns the API never maps
#       (see check_runner_column_parity) (the lagging-CHECK / lagging-enum class, #153 — e.g. the runner adding a
#       status value the fixture's CHECK still rejects).
#   (c) MAPPING-AHEAD (check_mapping_not_ahead_of_runner) — every PHYSICAL column the API's EF model MAPS
#       (.HasColumnName under a .ToTable in Data/SynthWatchDbContext.cs) must ALREADY exist in the runner schema.
#       EF projects each mapped scalar into its generated SELECT, so a column mapped-and-shipped BEFORE its runner
#       migration deploys makes prod issue `SELECT … col …` against a table without it → 42703 → 500. This is the
#       2026-07-23 /incidents outage (#286 mapped incidents.resolution_reason and auto-deployed before the runner's
#       0095 migration). (b) is BLIND to this — it treats a column the fixture has but the runner lacks as benign;
#       for a MAPPED column that reasoning is false, so (c) makes the DbContext mapping the source of truth. An
#       UNMAPPED extra column stays allowed (the legitimate drop-transition) — the mapped/unmapped split is the point.
# The runner schema is materialized from db/schema.sql + idempotent migrations-on-top (see load_runner_schema) —
# NOT a standalone numbered-migration replay, which was the earlier deferral's blocker. Requires the runner repo
# checked out at ./runner-repo. Runs in the `runner-parity` CI job, which #167 promoted INTO ci-gate's REQUIRED
# list (.github/workflows/ci-gate.yml) — so a real drift in EITHER direction holds the merge.
set -euo pipefail

PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5433}"
PGUSER="${PGUSER:-postgres}"
export PGPASSWORD="${PGPASSWORD:-pg}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURE="${FIXTURE:-$REPO_ROOT/tests/SynthWatch.Api.Tests/fixtures/schema.sql}"
# The EF mapping source parsed for MAPPED (table,column) pairs (direction (c), check_mapping_not_ahead_of_runner).
# Overridable so the self-test can point it at a synthetic mapping to prove the direction is load-bearing.
MAPPING_SRC="${MAPPING_SRC:-$REPO_ROOT/Data/SynthWatchDbContext.cs}"
# The runner OWNS its schema as db/schema.sql (the converged end-state for a NEW install; migration 0001's own
# header: "New installs get the same end state from db/schema.sql — the two must converge"). The numbered
# db/migrations/*.sql are IDEMPOTENT incremental patches for ALREADY-DEPLOYED DBs (applied via db/migrate.sh),
# so they do NOT replay standalone into an empty DB in filename order — that was the #164 deferral's blocker.
RUNNER_SCHEMA="${RUNNER_SCHEMA:-$REPO_ROOT/runner-repo/db/schema.sql}"
RUNNER_MIGRATIONS="${RUNNER_MIGRATIONS:-$REPO_ROOT/runner-repo/db/migrations}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

psql_db() { psql -v ON_ERROR_STOP=1 -qtA -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$1" "${@:2}"; }

wait_for_pg() {
  for _ in $(seq 1 30); do
    if pg_isready -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "postgres never became ready at $PGHOST:$PGPORT" >&2; exit 2
}

# Apply the fixture the SAME way PostgresFixture.cs does (defer body checks — pg_dump emits the SLA function
# before the tables its body references).
load_fixture() { # <db>
  psql_db "$1" -c "SET check_function_bodies = false;" -f "$FIXTURE" >/dev/null
}

# Materialize the CURRENT runner schema in a fresh DB: db/schema.sql (the converged base) FIRST, then every
# numbered migration applied IDEMPOTENTLY on top (IF NOT EXISTS / CREATE OR REPLACE, per db/migrate.sh's
# contract) to catch any migration newer than the last schema.sql sync. The CREATE ROLE shim runs first so a
# GRANT … TO "synthwatch-api" in schema.sql/a migration doesn't error (the diff never captures grants — the
# snapshot below is columns/constraints/functions only — so grants can't produce a false diff either).
load_runner_schema() { # <db>
  psql_db "$1" -c "DO \$\$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='synthwatch-api') THEN CREATE ROLE \"synthwatch-api\"; END IF; END \$\$;" >/dev/null
  if [ ! -f "$RUNNER_SCHEMA" ]; then
    echo "runner schema not found at $RUNNER_SCHEMA (is the runner repo checked out?)" >&2; exit 2
  fi
  psql_db "$1" -c "SET check_function_bodies = false;" -f "$RUNNER_SCHEMA" >/dev/null
  # Migrations on top are idempotent, so newest-wins regardless of whether schema.sql or a migration leads.
  if [ -d "$RUNNER_MIGRATIONS" ]; then
    local f
    for f in $(ls "$RUNNER_MIGRATIONS"/*.sql | sort); do
      psql_db "$1" -c "SET check_function_bodies = false;" -f "$f" >/dev/null
    done
  fi
}

# Normalized catalog snapshot of the PUBLIC schema. Columns (table/name/type/nullable), table/domain
# constraints, and full function definitions (signature + body). schema_migrations is runner bookkeeping the
# API never reads — skip. Two deliberate NORMALIZATIONS keep the gate on SCHEMA SHAPE the API reads and off
# noise (precision added in #167 after the first RUNNER_PARITY run over-flagged both classes):
#   • Column DEFAULT is NOT snapshotted. A default is a runner-owned INSERT-time convention, not a shape the
#     API reads (the API supplies values on insert; it never depends on the DB default). Diffing it turned a
#     harmless divergence into a red (checks.failure_threshold default 1-runner vs 3-fixture; reconcile_apply_
#     plan.id nextval-vs-identity). Nullability + type ARE snapshotted, so a real "can't INSERT this" shape
#     drift (e.g. runs.location NOT NULL) is still caught. NOTE: the failure_threshold default divergence is a
#     real API-vs-runner disagreement (the API entity Check.cs also defaults 3) — surfaced as its OWN finding
#     in the PR, NOT enforced here.
#   • Function bodies are COMMENT- and WHITESPACE-normalized before diffing (strip `-- …` line comments, then
#     strip whitespace + statement `;`). pg_get_functiondef preserves the stored body verbatim, so the
#     fixture stripping the runner's inline comments (or a trailing `;`) read as a body diff though the LOGIC
#     was byte-identical (slo_burn_status). A REAL token/logic change still survives normalization → still red
#     (proven by --self-test). Caveat: a `--` inside a string literal would be stripped too; none of the
#     runner's functions contain one.
snapshot() { # <db> <outfile>
  {
    psql_db "$1" -c "
      SELECT 'col|'||table_name||'|'||column_name||'|'||data_type||'|'||is_nullable
      FROM information_schema.columns
      WHERE table_schema='public' AND table_name <> 'schema_migrations'
      ORDER BY 1;"
    psql_db "$1" -c "
      SELECT 'con|'||rel.relname||'|'||con.conname||'|'||pg_get_constraintdef(con.oid)
      FROM pg_constraint con
      JOIN pg_class rel ON rel.oid=con.conrelid
      JOIN pg_namespace n ON n.oid=rel.relnamespace
      WHERE n.nspname='public' AND rel.relname <> 'schema_migrations'
      ORDER BY 1;"
    psql_db "$1" -c "
      SELECT 'fn|'||p.proname||'|'||
             regexp_replace(regexp_replace(pg_get_functiondef(p.oid), '--[^'||chr(10)||']*', '', 'g'),
                            '[[:space:];]+', '', 'g')
      FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
      WHERE n.nspname='public'
      ORDER BY 1;"
  } | LC_ALL=C sort > "$2"   # ★ authoritative re-sort under a FIXED collation: psql's ORDER BY uses the
                             # server collation, comm below uses the shell's — they must agree or comm mis-diffs.
}

# Report rows present in the runner schema (A) but MISSING/DIFFERENT in the fixture (B). The reverse (fixture
# has extra objects) is NOT a failure — the fixture may keep a table the API reads that a migration later drops,
# and extra objects can't cause a read to 500. We only fail on "the API's mirror lacks what the runner defines".
diff_missing_in_fixture() { # <runnerSnap> <fixtureSnap>  → prints missing rows, returns 1 if any
  # snapshots are already LC_ALL=C-sorted; run comm under the same fixed collation so the merge can't disagree.
  LC_ALL=C comm -23 "$1" "$2"
}

# (a) DbContext presence — every physical table the API maps MUST exist in the fixture. Needs NO runner repo,
# so it runs even when the runner checkout is absent (local dev), and it's what catches run_requests.
check_dbcontext_presence() { # <fixtureDb>
  local ctx="$REPO_ROOT/Data/SynthWatchDbContext.cs" mapped present missing
  mapped="$(grep -oE '\.ToTable\("[a-z_]+"\)' "$ctx" | grep -oE '"[a-z_]+"' | tr -d '"' | LC_ALL=C sort -u)"
  present="$(psql_db "$1" -c "SELECT table_name FROM information_schema.tables WHERE table_schema='public'" | LC_ALL=C sort -u)"
  missing="$(LC_ALL=C comm -23 <(echo "$mapped") <(echo "$present"))"
  if [ -n "$missing" ]; then
    echo "❌ Schema parity FAILED — tables mapped in Data/SynthWatchDbContext.cs are MISSING from the fixture:" >&2
    echo "$missing" | sed 's/^/   /' >&2
    echo "   → a handler that reads one of these compiles green but is integration-untestable (the #129 class)." >&2
    echo "   Add the table to tests/SynthWatch.Api.Tests/fixtures/schema.sql as a hand-appended block." >&2
    return 1
  fi
  echo "✅ DbContext presence OK — every mapped table exists in the fixture."
}

# (b) Runner column parity — for tables present in BOTH the runner schema and the fixture, the fixture must
# carry every column/constraint the runner defines (catches the lagging-CHECK / missing-column class, #153).
# Scoped to SHARED tables ON PURPOSE: the fixture is a curated subset; a runner table the API never reads is
# legitimately absent and must NOT fail the gate.
check_runner_column_parity() { # <migDb> <fixDb>
  snapshot "$1" "$WORK/mig.txt"
  snapshot "$2" "$WORK/fix.txt"
  local fixobjs missing
  # SHARED-object rule for BOTH tables and functions: an object (table or function) the fixture doesn't define
  # is one the API doesn't read — a runner-only addition must NOT fail the gate (the false-positive the scoping
  # exists to prevent). fn|/col|/con| rows all key on the object name in field 2.
  fixobjs="$( { psql_db "$2" -c "SELECT DISTINCT table_name FROM information_schema.columns WHERE table_schema='public'";
                psql_db "$2" -c "SELECT proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public'"; } | LC_ALL=C sort -u)"
  # ★ RUNNER-INTERNAL ALLOWLIST — a DECISION, not an accident (each entry reviewed, keyed "table/object"). These
  # columns/constraints live on a SHARED table but are NOT mapped by the API's Data/SynthWatchDbContext.cs, so the
  # fixture legitimately omits them — the SAME rationale that already exempts a whole runner-only table (the
  # fixture is a curated subset of what the API reads). Without this, every runner-internal column on a shared
  # table false-fails a column-scoped gate. A NEW shared-table object stays strict (red) until it is either
  # patched into the fixture or explicitly added here — so the exemption list is auditable, never silent.
  #   checks.baseline_screenshot_url, checks.last_burn_notified_at, incidents.rca_notified_at — runner-internal
  #   visual-baseline / notification bookkeeping columns the API never maps.
  #   (checks.retries + its CHECK were removed here when the runner dropped the dead fast-retry column in 0084 —
  #    the runner no longer defines them, so the allow entries filtered nothing and would rot silently.)
  local allow="checks/baseline_screenshot_url checks/last_burn_notified_at incidents/rca_notified_at"
  missing="$(diff_missing_in_fixture "$WORK/mig.txt" "$WORK/fix.txt" | while IFS='|' read -r kind obj rest; do
    grep -qx "$obj" <<< "$fixobjs" || continue           # object (table/fn) the API doesn't read at all → skip
    # ↑ here-string, not `echo "$fixobjs" | grep -qx`: grep -q closes the pipe on match; under pipefail a large
    #   $fixobjs makes echo take SIGPIPE (141) → `|| continue` would SKIP a shared object that IS in the fixture
    #   → the parity gate silently drops it (false-green). A here-string has no piped writer to kill.
    key="$obj/${rest%%|*}"                               # "table/column" or "table/constraint" (fn: name/1st-token, never matches)
    case " $allow " in *" $key "*) continue;; esac       # vetted runner-internal object → exempt
    echo "$kind|$obj|$rest"
  done)"
  if [ -n "$missing" ]; then
    echo "❌ Schema parity FAILED — shared objects differ from the runner schema (fixture is missing):" >&2
    echo "$missing" | sed 's/^/   /' >&2
    echo "   Update the fixture block to match the runner's migration — do NOT regenerate the file." >&2
    return 1
  fi
  echo "✅ Runner column parity OK — shared tables/functions match the runner schema."
}

# Enumerate the (table, column) pairs the API's EF model MAPS to PHYSICAL columns: every `.HasColumnName("col")`
# that sits inside a `modelBuilder.Entity<T>(e => { … })` block whose table is fixed by `.ToTable("tbl")`. EF
# projects every mapped scalar into its generated SELECT, so a prod query names each of these columns — that is
# exactly the set whose ABSENCE from the live schema yields a 42703 (undefined_column) → 500.
#
# ── HOW columns are enumerated (item 2 of the brief) ──
# A pure TEXT parse of $MAPPING_SRC (Data/SynthWatchDbContext.cs). Each entity block opens with
# `modelBuilder.Entity<…>` (→ forget the current table) and, for a PHYSICAL table, names it with `.ToTable("tbl")`
# BEFORE its `.HasColumnName` calls (verified: all 23 mapped tables follow ToTable-then-columns). We bind every
# subsequent HasColumnName to that table until the next `Entity<` resets it. Chosen over reflecting the built EF
# model because it needs no build/runtime and no DB — it runs in the same psql-only CI job as the rest of the gate.
#
# ── WHAT IT DELIBERATELY MISSES — stated plainly, because naming the blind spots matters more than claiming
#    full coverage ──
#   • Convention-mapped columns with NO explicit `.HasColumnName`. EF would still SELECT them. This codebase maps
#     snake_case columns, which EF's PascalCase convention never produces on its own, so every physical column
#     here carries an explicit HasColumnName — but a FUTURE property mapped purely by convention is invisible here.
#   • Shadow properties (mapped with no CLR property, hence no HasColumnName) — same blind spot.
#   • Owned types / value-converted columns declared via OwnsOne — their HasColumnName (if any) binds to the
#     OWNER's ToTable, which is usually right, but a differently-tabled owned type could mis-bind. None exist today.
#   • Keyless query types (`HasNoKey()` + `ToView(null)` — 28 of them) map RESULT-SET columns of RAW SQL, not
#     physical table columns; they carry no ToTable, so this parser SKIPS them (correct — their "columns" must
#     NOT be diffed against runner tables, or every raw-SQL alias would false-fail).
mapped_table_columns() { # → "table<TAB>column" lines, one per mapped PHYSICAL column
  local src="$MAPPING_SRC" line tbl="" col
  while IFS= read -r line; do
    case "$line" in
      *"modelBuilder.Entity<"*) tbl="" ;;                  # new entity block → table unknown until its ToTable
    esac
    case "$line" in
      *'.ToTable("'*) tbl="$(sed -nE 's/.*\.ToTable\("([a-z_]+)"\).*/\1/p' <<< "$line")" ;;
    esac
    case "$line" in
      *'.HasColumnName("'*)
        [ -n "$tbl" ] || continue                          # inside a ToView/keyless block → not a physical column
        col="$(sed -nE 's/.*\.HasColumnName\("([a-z_]+)"\).*/\1/p' <<< "$line")"
        [ -n "$col" ] && printf '%s\t%s\n' "$tbl" "$col"
        ;;
    esac
  done < "$src"
}

# (c) MAPPING-AHEAD-OF-MIGRATION — the direction (b)/diff_missing_in_fixture structurally CANNOT see. (b) fails
# only when the FIXTURE lags the runner; it treats a column the fixture HAS but the runner LACKS as benign
# ("extra objects can't 500"). That reasoning is FALSE for a column the EF model MAPS: EF projects it into the
# generated SELECT, so if runner main (origin/main) has not yet added it, prod runs `SELECT … col …` against a
# table without the column → 42703 → 500 (the 2026-07-23 /incidents outage: #286 mapped incidents.resolution_reason
# and shipped before the runner's 0095 migration deployed). This check makes the API's OWN mapping the source of
# truth: every MAPPED physical column must already exist in the runner schema. An UNMAPPED extra column in the
# fixture stays allowed (the legitimate drop-transition — a column the runner will remove but the fixture still
# carries), because it is never SELECTed. Scoped to SHARED tables: a mapped table the runner does not define at
# all is a table-level concern (handled by the presence gate / a possibly API-only construct), not this direction.
check_mapping_not_ahead_of_runner() { # <migDb>  — the materialized runner schema (origin/main)
  snapshot "$1" "$WORK/mig_map.txt"
  local runnertables runnercols pairs ahead
  runnertables="$(grep '^col|' "$WORK/mig_map.txt" | awk -F'|' '{print $2}' | LC_ALL=C sort -u)"
  runnercols="$(grep '^col|' "$WORK/mig_map.txt" | awk -F'|' '{print $2"/"$3}' | LC_ALL=C sort -u)"
  pairs="$(mapped_table_columns | LC_ALL=C sort -u)"
  # ★ FAIL-CLOSED when the check cannot actually compare (the verify()-must-go-red lesson): an empty runner
  #   snapshot or an empty mapped-pairs parse means the comparison asserts NOTHING — that must be RED, not a
  #   vacuous green, or the gate manufactures confidence.
  if [ -z "$runnertables" ]; then
    echo "❌ mapping-ahead check ABORTED — the runner schema snapshot has NO columns; cannot compare (fail-closed)." >&2
    return 1
  fi
  if [ -z "$pairs" ]; then
    echo "❌ mapping-ahead check ABORTED — parsed ZERO mapped columns from $MAPPING_SRC; the parser is broken (fail-closed)." >&2
    return 1
  fi
  ahead="$(while IFS=$'\t' read -r t c; do
    [ -n "$t" ] || continue
    grep -qxF "$t" <<< "$runnertables" || continue         # table the runner doesn't define → not THIS direction
    #   ↑ here-string, NOT a pipe into `grep -q` (the SIGPIPE-under-pipefail class the rest of this file guards):
    #     grep -q closes the pipe on match → a piped writer takes 141 → `||`/`&&` misfires.
    grep -qxF "$t/$c" <<< "$runnercols" && continue         # mapped column present in the runner → fine
    printf '%s.%s\n' "$t" "$c"
  done <<< "$pairs" | LC_ALL=C sort -u)"
  if [ -n "$ahead" ]; then
    echo "❌ Schema parity FAILED — the EF model MAPS columns the runner schema (origin/main) does NOT define:" >&2
    echo "$ahead" | sed 's/^/   /' >&2
    echo "   → EF projects every mapped scalar, so PRODUCTION will run  SELECT … <col> … FROM <table>  against a" >&2
    echo "     table WITHOUT the column → Postgres 42703 (undefined_column) → that endpoint 500s. This is exactly" >&2
    echo "     the 2026-07-23 /incidents outage: a column mapped in Data/SynthWatchDbContext.cs shipped BEFORE its" >&2
    echo "     runner migration reached prod." >&2
    echo "   REMEDY — the runner migration that adds the column must land in prod FIRST, in this order:" >&2
    echo "     1. merge the paired runner PR (the ALTER TABLE … ADD COLUMN migration) to runner main;" >&2
    echo "     2. run the runner deploy and CONFIRM the column exists in prod;" >&2
    echo "     3. THEN merge this API PR." >&2
    echo "   (An UNMAPPED extra column in the fixture is fine — this fires ONLY for columns the DbContext maps," >&2
    echo "    i.e. columns a prod SELECT will actually name. Un-map the column here if it truly must not be read yet.)" >&2
    return 1
  fi
  echo "✅ Mapping-ahead check OK — every column the EF model maps exists in the runner schema."
}

run_parity() {
  wait_for_pg
  psql_db postgres -c "DROP DATABASE IF EXISTS fix;" -c "CREATE DATABASE fix;" >/dev/null
  load_fixture fix
  local rc=0
  check_dbcontext_presence fix || rc=1
  # Runner column parity is OPT-IN (RUNNER_PARITY=1). The REQUIRED presence gate leaves it off; the separate
  # (non-required) runner-parity CI job sets it on. Materializes the runner schema from db/schema.sql +
  # idempotent migrations (load_runner_schema), then diffs shared objects.
  if [ "${RUNNER_PARITY:-}" = "1" ]; then
    if [ ! -f "$RUNNER_SCHEMA" ]; then
      echo "RUNNER_PARITY=1 but no runner schema at $RUNNER_SCHEMA" >&2; exit 2
    fi
    psql_db postgres -c "DROP DATABASE IF EXISTS mig;" -c "CREATE DATABASE mig;" >/dev/null
    load_runner_schema mig
    check_runner_column_parity mig fix || rc=1
    # (c) MAPPING-AHEAD — a column the EF model maps that runner main does NOT yet define (the #286 outage class).
    #     Diffs the DbContext mapping directly against the runner schema; does NOT touch (b)'s fixture-behind path.
    check_mapping_not_ahead_of_runner mig || rc=1
  fi
  [ "$rc" -eq 0 ] && echo "✅ Schema parity OK." || exit 1
}

run_self_test() {
  wait_for_pg
  echo "self-test: identical schemas must be GREEN, a dropped column must go RED."
  psql_db postgres -c "DROP DATABASE IF EXISTS st_a;" -c "CREATE DATABASE st_a;" >/dev/null
  psql_db postgres -c "DROP DATABASE IF EXISTS st_b;" -c "CREATE DATABASE st_b;" >/dev/null
  load_fixture st_a
  load_fixture st_b
  snapshot st_a "$WORK/a.txt"
  snapshot st_b "$WORK/b.txt"
  if [ -n "$(diff_missing_in_fixture "$WORK/a.txt" "$WORK/b.txt" || true)" ]; then
    echo "self-test BUG: identical fixtures reported drift" >&2; exit 3
  fi
  echo "  identical → GREEN ✓"
  # Drop a real column from B and confirm the engine flags exactly it. runs.retry_count is a runner-added
  # column the API reads (the #152 attempt-count semantics) — a faithful stand-in for a real drift.
  # BARE drop (no CASCADE): as of runner 0083 the fixture's countable_run view is an EXPLICIT column list, not
  # `SELECT *`, so it no longer pins a hard dependency on every runs column. A bare `DROP COLUMN retry_count`
  # now succeeds — and KEEPING it bare is deliberate: if a future change reintroduces the `SELECT *` coupling,
  # this line errors and the self-test goes red, catching the regression. (This unblocks the dead-retries
  # cleanup: dropping retry_count for real is no longer gated on a view rewrite.)
  psql_db st_b -c "ALTER TABLE runs DROP COLUMN retry_count;" >/dev/null
  snapshot st_b "$WORK/b2.txt"
  local drift
  drift="$(diff_missing_in_fixture "$WORK/a.txt" "$WORK/b2.txt" || true)"
  if ! grep -q '^col|runs|retry_count|' <<< "$drift"; then     # here-string, not `echo | grep -q` (SIGPIPE class)
    echo "self-test FAILED: dropping runs.retry_count was NOT detected" >&2
    echo "$drift" >&2; exit 3
  fi
  echo "  dropped runs.retry_count → RED, detected ✓ (case 8: fixture BEHIND runner still RED)"

  # ── ★ FUNCTION-BODY NORMALIZATION must-go-red: prove the comment/whitespace stripping (the slo_burn_status
  #    false-positive fix) does NOT disable real drift detection. A comment/whitespace-only body change must
  #    stay GREEN; a real LOGIC change (41+1 → 41+2) must go RED. The `--` comment sits on its OWN line so it
  #    terminates at the newline exactly as the normalization regex expects. ──
  psql_db st_a -c "CREATE FUNCTION public.st_probe() RETURNS int LANGUAGE sql AS \$q\$ SELECT 41 + 1 \$q\$;" >/dev/null
  psql_db st_b -c "$(printf 'CREATE FUNCTION public.st_probe() RETURNS int LANGUAGE sql AS $q$\n -- a comment the other side lacks\n SELECT 41  +  1 $q$;')" >/dev/null
  snapshot st_a "$WORK/a3.txt"
  snapshot st_b "$WORK/b3.txt"
  if grep -q '^fn|st_probe|' <<< "$(diff_missing_in_fixture "$WORK/a3.txt" "$WORK/b3.txt")"; then  # here-string (SIGPIPE class)
    echo "self-test FAILED: a comment/whitespace-only function change was flagged — normalization is broken" >&2; exit 3
  fi
  echo "  comment/whitespace-only fn change → GREEN (normalization holds) ✓"
  psql_db st_b -c "CREATE OR REPLACE FUNCTION public.st_probe() RETURNS int LANGUAGE sql AS \$q\$ SELECT 41 + 2 \$q\$;" >/dev/null
  snapshot st_b "$WORK/b4.txt"
  if ! grep -q '^fn|st_probe|' <<< "$(diff_missing_in_fixture "$WORK/a3.txt" "$WORK/b4.txt")"; then  # here-string (SIGPIPE class)
    echo "self-test FAILED: a function LOGIC change (41+1 → 41+2) was NOT detected — normalization is too aggressive" >&2; exit 3
  fi
  echo "  logic fn change (41+1 → 41+2) → RED, detected ✓"

  # ── ★ MAPPING-AHEAD-OF-MIGRATION must-go-red — reproduce the 2026-07-23 /incidents outage (#286) and prove the
  #    NEW direction (c) catches what the pre-existing fixture-behind gate (b) structurally cannot. ENGINE test,
  #    no runner checkout needed: the fixture stands in for the runner schema (as the retry_count drop already
  #    does). Topology of the outage:
  #      • runner main (origin/main) LACKS the mapped column  → here: fixture MINUS incidents.resolution_reason
  #      • this PR's DbContext MAPS it (it does today — #286 added the mapping) → EF SELECTs it → 42703 in prod
  echo "mapping-ahead (direction c): reproducing the #286 /incidents outage class."
  psql_db postgres -c "DROP DATABASE IF EXISTS st_mig;" -c "CREATE DATABASE st_mig;" >/dev/null
  load_fixture st_mig
  psql_db st_mig -c "ALTER TABLE incidents DROP COLUMN resolution_reason;" >/dev/null   # = runner main pre-0095

  # (case 9, part 1) the PRE-EXISTING fixture-behind gate (b) is BLIND to this. diff_missing_in_fixture mig→fix
  #   reports only columns the runner HAS that the fixture LACKS; an extra-in-fixture MAPPED column is the reverse
  #   direction, which (b) calls benign. With fix = the untouched full fixture (st_a) and st_mig missing the
  #   column, (b) must report NOTHING about resolution_reason — the exact hole that let #286 through.
  snapshot st_mig "$WORK/mig_ahead.txt"
  if grep -q 'resolution_reason' <<< "$(diff_missing_in_fixture "$WORK/mig_ahead.txt" "$WORK/a.txt" || true)"; then
    echo "self-test BUG: the fixture-behind direction unexpectedly flagged the extra-in-fixture column" >&2; exit 3
  fi
  echo "  (b) fixture-behind gate → GREEN on the mapping-ahead column (the #286 hole reproduced) ✓"

  # (case 6) the NEW direction (c) must go RED and NAME incidents.resolution_reason.
  local ahead_out
  ahead_out="$(check_mapping_not_ahead_of_runner st_mig 2>&1 || true)"
  if ! grep -q 'incidents\.resolution_reason' <<< "$ahead_out"; then
    echo "self-test FAILED: mapping-ahead check did NOT flag incidents.resolution_reason" >&2
    echo "$ahead_out" >&2; exit 3
  fi
  echo "  (c) mapped column absent from runner → RED, named incidents.resolution_reason ✓ (case 6 — the #286 catch)"

  # (case 7) an UNMAPPED absent column stays GREEN — the drop-transition must survive. Full fixture minus a column
  #   the DbContext does NOT map: incidents.notify_status is on the SAME shared table as resolution_reason but has
  #   NO .HasColumnName, so it is a genuine paired contrast — drop the MAPPED sibling → RED (case 6), drop this
  #   UNMAPPED one → GREEN. (c) iterates MAPPED pairs only, so an unmapped absent column can never be named → the
  #   WHOLE check is GREEN. This is the mapped/unmapped distinction, load-bearing: without it a legitimate runner
  #   column-drop (fixture still carrying the column, API no longer reading it) would start blocking the merge.
  psql_db postgres -c "DROP DATABASE IF EXISTS st_unmapped;" -c "CREATE DATABASE st_unmapped;" >/dev/null
  load_fixture st_unmapped
  psql_db st_unmapped -c "ALTER TABLE incidents DROP COLUMN notify_status;" >/dev/null   # in fixture, UNMAPPED
  if check_mapping_not_ahead_of_runner st_unmapped >/dev/null 2>&1; then
    echo "  (c) UNMAPPED absent column (incidents.notify_status) → GREEN (drop-transition survives) ✓ (case 7)"
  else
    echo "self-test FAILED: dropping an UNMAPPED column made the mapping-ahead check RED — it is no longer scoped" >&2
    echo "  to MAPPED columns; the legitimate drop-transition would start blocking:" >&2
    check_mapping_not_ahead_of_runner st_unmapped >&2 || true; exit 3
  fi

  # (case 9, part 2) PERTURBATION — remove the new direction and show case 6 goes GREEN, then restore → RED again.
  #   We swap MAPPING_SRC to a DbContext-shaped file that maps ONLY a column the stand-in still HAS (checks.name),
  #   i.e. maps NOTHING that is absent → (c) finds no ahead-column → GREEN. Restoring the real DbContext returns
  #   it to RED. This proves the RED in case 6 is CAUSED by the DbContext actually MAPPING the absent column — the
  #   direction is load-bearing, not vacuous.
  local perturb="$WORK/perturb_ctx.cs"
  printf '%s\n' 'modelBuilder.Entity<X>(e =>' '{' '    e.ToTable("checks");' '    e.Property(x => x.Name).HasColumnName("name");' '});' > "$perturb"
  if MAPPING_SRC="$perturb" check_mapping_not_ahead_of_runner st_mig >/dev/null 2>&1; then
    echo "  (case 9) PERTURB: new direction neutered (maps only a present column) → case 6 GREEN ✓"
  else
    echo "self-test FAILED: perturbation should be GREEN (no mapped column is absent) but it went RED" >&2; exit 3
  fi
  if check_mapping_not_ahead_of_runner st_mig >/dev/null 2>&1; then
    echo "self-test FAILED: after restoring the real DbContext, case 6 should be RED again but was GREEN" >&2; exit 3
  fi
  echo "  (case 9) PERTURB restored: real DbContext → case 6 RED again ✓ (direction c is load-bearing)"

  echo "✅ self-test passed — the catalog diff detects a missing column + a real function-logic change,"
  echo "   ignores comment/whitespace-only function noise, and the mapping-ahead direction (c) catches a MAPPED"
  echo "   column absent from the runner (the #286 class) while leaving an UNMAPPED drop-transition green."
}

case "${1:-}" in
  --self-test) run_self_test ;;
  *)           run_parity ;;
esac
