#!/usr/bin/env bash
# assert-tests-ran.selftest.sh — MUST-GO-RED coverage for scripts/assert-tests-ran.py.
#
# ★ WHY. The guard this exercises spent its entire life exiting 1 because its own parse was broken, which
#   is indistinguishable from it CATCHING a real skip. A guard that can only ever go red is as useless as
#   one that can only ever go green — so assert BOTH directions on fixed fixtures: the green case must
#   pass, and every failure mode must actually fail. Without this, "the guard is green" proves nothing.
#
# Fixtures live in scripts/testdata/trx/ and are hand-written to mirror REAL shapes:
#   good.trx       — reproduces the observed CI run: total=518, 2 TraceSignalsGoldenParity skips that are
#                    BY DESIGN in that job. This is the case a whole-suite "zero skips" rule got wrong.
#   db-skipped.trx — the failure this whole job exists to catch: DB-backed tests NotExecuted.
#   empty.trx      — a run that discovered nothing (total=0).
#   garbage.trx    — an unparseable results file.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GUARD="$ROOT/scripts/assert-tests-ran.py"
DATA="$ROOT/scripts/testdata/trx"

fails=0

# expect <want-rc> <fixture> <description> [required-substring]
expect() {
  local want="$1" fixture="$2" desc="$3" needle="${4:-}"
  local out rc
  out="$(python3 "$GUARD" "$DATA/$fixture" 2>&1)"; rc=$?
  if [ "$rc" -ne "$want" ]; then
    echo "❌ $desc: expected rc=$want, got rc=$rc"
    printf '%s\n' "$out" | sed 's/^/     /'
    fails=1
    return
  fi
  if [ -n "$needle" ] && ! grep -qF "$needle" <<<"$out"; then
    echo "❌ $desc: rc=$want as expected, but output lacked \"$needle\""
    printf '%s\n' "$out" | sed 's/^/     /'
    fails=1
    return
  fi
  echo "✅ $desc (rc=$rc)"
}

echo "── must-go-GREEN ──"
# The guard must READ the numbers, not just exit 0 — assert the echoed counts, so a guard that silently
# stopped parsing can never masquerade as a pass.
expect 0 good.trx       "real CI shape passes (518 tests, 2 by-design non-DB skips)" "suite: total=518"
expect 0 good.trx       "…and reports the DB-backed tests it saw run"                "db-backed: ran=2 skipped=0"

echo "── must-go-RED ──"
expect 1 db-skipped.trx "DB-backed tests skipped → RED"    "DB-BACKED test(s) SKIPPED"
expect 1 db-skipped.trx "…and names the skipped tests"     "skipped: SynthWatch.Api.Tests.IntegrationTests."
expect 1 empty.trx      "no tests discovered → RED"        "total=0"
expect 1 garbage.trx    "unparseable trx → RED"            "could not read/parse"
expect 1 does-not-exist.trx "missing trx → RED"            "could not read/parse"

if [ "$fails" -ne 0 ]; then
  echo "::error::assert-tests-ran.py self-test FAILED — the silent-skip guard does not behave as specified."
  exit 1
fi
echo "✅ assert-tests-ran.py passes green on a real suite and goes red on every failure mode."
