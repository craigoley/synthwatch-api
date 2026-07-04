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
# Opt-in (RUNNER_PARITY=1): also diff shared-table COLUMNS against the runner's migrations (the lagging-CHECK
# class, #153). Left OFF by default because the runner's numbered migrations do NOT replay cleanly into an
# empty DB in filename order (e.g. 0001_run_metrics references runs, created later) — enabling it needs the
# runner's real replay order (its base schema / migration-runner ordering), which lives in the runner repo.
# The catalog-diff engine itself is proven by --self-test; only the migration-REPLAY half is deferred.
set -euo pipefail

PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5433}"
PGUSER="${PGUSER:-postgres}"
export PGPASSWORD="${PGPASSWORD:-pg}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURE="${FIXTURE:-$REPO_ROOT/tests/SynthWatch.Api.Tests/fixtures/schema.sql}"
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

# The synthwatch-api role the runner GRANTs target — create it before applying migrations so GRANT … TO
# "synthwatch-api" doesn't error (the fixture deliberately omits grants; this shim is grants-only scaffolding).
load_runner_migrations() { # <db>
  psql_db "$1" -c "SET check_function_bodies = false;" \
                 -c "DO \$\$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='synthwatch-api') THEN CREATE ROLE \"synthwatch-api\"; END IF; END \$\$;" >/dev/null
  if [ ! -d "$RUNNER_MIGRATIONS" ]; then
    echo "runner migrations not found at $RUNNER_MIGRATIONS (is the runner repo checked out?)" >&2; exit 2
  fi
  # Lexical order = migration order (zero-padded 0001_…), matching check-pg-grant-coverage.mjs.
  local f
  for f in $(ls "$RUNNER_MIGRATIONS"/*.sql | sort); do
    psql_db "$1" -c "SET check_function_bodies = false;" -f "$f" >/dev/null
  done
}

# Normalized catalog snapshot of the PUBLIC schema. Columns (table/name/type/nullable/default), table/domain
# constraints, and full function definitions (signature + body, via pg_get_functiondef — so a body-only change
# is caught too). schema_migrations is runner bookkeeping the API never reads — skip.
snapshot() { # <db> <outfile>
  {
    psql_db "$1" -c "
      SELECT 'col|'||table_name||'|'||column_name||'|'||data_type||'|'||is_nullable||'|'||coalesce(column_default,'')
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
      SELECT 'fn|'||p.proname||'|'||replace(pg_get_functiondef(p.oid), E'\n', ' ')
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
  missing="$(diff_missing_in_fixture "$WORK/mig.txt" "$WORK/fix.txt" | while IFS='|' read -r kind obj rest; do
    if echo "$fixobjs" | grep -qx "$obj"; then echo "$kind|$obj|$rest"; fi
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
  # Runner column parity is OPT-IN (RUNNER_PARITY=1) — deferred until the runner's real migration replay order
  # is available (its numbered migrations don't replay cleanly into an empty DB in filename order). CI leaves it
  # off; the catalog-diff engine it uses is proven by --self-test.
  if [ "${RUNNER_PARITY:-}" = "1" ]; then
    if [ ! -d "$RUNNER_MIGRATIONS" ]; then
      echo "RUNNER_PARITY=1 but no runner migrations at $RUNNER_MIGRATIONS" >&2; exit 2
    fi
    psql_db postgres -c "DROP DATABASE IF EXISTS mig;" -c "CREATE DATABASE mig;" >/dev/null
    load_runner_migrations mig
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
  psql_db st_b -c "ALTER TABLE runs DROP COLUMN retry_count;" >/dev/null
  snapshot st_b "$WORK/b2.txt"
  local drift
  drift="$(diff_missing_in_fixture "$WORK/a.txt" "$WORK/b2.txt" || true)"
  if ! echo "$drift" | grep -q '^col|runs|retry_count|'; then
    echo "self-test FAILED: dropping runs.retry_count was NOT detected" >&2
    echo "$drift" >&2; exit 3
  fi
  echo "  dropped runs.retry_count → RED, detected ✓"
  echo "✅ self-test passed — the catalog diff detects a missing column."
}

case "${1:-}" in
  --self-test) run_self_test ;;
  *)           run_parity ;;
esac
