#!/usr/bin/env bash
# Schema-parity gate (M3c) — catch the "fixture drifted from the runner schema" class at CI instead of as a
# silent coverage hole (the #129 reconcile_apply_plan miss) or a prod 500. The runner repo OWNS the schema;
# this API's tests/fixtures/schema.sql is a hand-maintained mirror. This job asserts the mirror is faithful.
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
# schema (the lagging-CHECK / lagging-enum class, #153 — e.g. the runner adding a status value the fixture's
# CHECK still rejects). The runner schema is materialized from db/schema.sql + idempotent migrations-on-top
# (see load_runner_schema) — NOT a standalone numbered-migration replay, which was the earlier deferral's
# blocker. Requires the runner repo checked out at ./runner-repo. Runs in a SEPARATE, non-required CI job so a
# real drift surfaces as a visible red without wedging the REQUIRED presence gate.
set -euo pipefail

PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5433}"
PGUSER="${PGUSER:-postgres}"
export PGPASSWORD="${PGPASSWORD:-pg}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURE="${FIXTURE:-$REPO_ROOT/tests/SynthWatch.Api.Tests/fixtures/schema.sql}"
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
  #   checks.retries (+ its CHECK)  — fast-retry config the runner owns; API never reads it.
  #   checks.baseline_screenshot_url, checks.last_burn_notified_at, incidents.rca_notified_at — runner-internal
  #   visual-baseline / notification bookkeeping columns the API never maps.
  local allow="checks/retries checks/checks_retries_check checks/baseline_screenshot_url checks/last_burn_notified_at incidents/rca_notified_at"
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
  # CASCADE: the fixture's countable_run view (runner 0081) is `SELECT * FROM runs`, so it pins a hard
  # dependency on EVERY runs column; a bare DROP now errors under `bash -e`. CASCADE also drops the view, but
  # the `col|runs|retry_count|` drift line is still emitted, so the grep assertion below is unchanged — this
  # adapts the negative control to the new (faithful) view, it does NOT weaken what it proves.
  psql_db st_b -c "ALTER TABLE runs DROP COLUMN retry_count CASCADE;" >/dev/null
  snapshot st_b "$WORK/b2.txt"
  local drift
  drift="$(diff_missing_in_fixture "$WORK/a.txt" "$WORK/b2.txt" || true)"
  if ! grep -q '^col|runs|retry_count|' <<< "$drift"; then     # here-string, not `echo | grep -q` (SIGPIPE class)
    echo "self-test FAILED: dropping runs.retry_count was NOT detected" >&2
    echo "$drift" >&2; exit 3
  fi
  echo "  dropped runs.retry_count → RED, detected ✓"

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
  echo "✅ self-test passed — the catalog diff detects a missing column + a real function-logic change,"
  echo "   and ignores comment/whitespace-only function noise."
}

case "${1:-}" in
  --self-test) run_self_test ;;
  *)           run_parity ;;
esac
