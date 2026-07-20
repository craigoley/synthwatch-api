#!/usr/bin/env python3
"""assert-tests-ran.py — fail closed when the DB-backed tests SILENTLY SKIPPED.

WHY THIS EXISTS. The DB-backed tests (`[Collection("postgres")]`) call `Skip.IfNot(_pg.Available, …)`,
so when the fixture cannot reach a Postgres they SKIP rather than fail and the suite still reports
GREEN. That is not hypothetical: four DB-backed tests in the preview-credentials PR were written,
reviewed and pushed having NEVER ONCE EXECUTED — CI was their first real run, and all four failed.
A green "Test" job is therefore NOT evidence the DB tests ran; assert it separately, here.

★ WHY NOT grep. The previous inline guard was
      skipped=$(grep -o 'skipped="[0-9]*"' all.trx | head -1 | tr -d 'a-z="')
  which was broken THREE ways at once and had never once executed past its first line:
    1. A TRX `<Counters>` element HAS NO `skipped` ATTRIBUTE. The schema is
       total/executed/passed/failed/error/timeout/aborted/inconclusive/passedButRunAborted/
       notRunnable/notExecuted/… — skipped tests are counted in `notExecuted`. So the grep matched
       nothing, exited 1, and `set -euo pipefail` killed the step at that line — before the echo.
       That, NOT the SIGPIPE below, is why the step went red.
    2. `| head -1` is the repo's banned fail-open SIGPIPE antipattern (scripts/check-no-sigpipe-grep.sh):
       head closes the pipe on line 1, the still-writing grep takes SIGPIPE (141), and pipefail makes
       the pipeline exit 141 even on a successful match. Latent here, fatal elsewhere (cf. runner #155,
       #279, #283).
    3. `notExecuted != 0` is the WRONG PREDICATE anyway — TraceSignalsGoldenParityTests skips 2 tests
       BY DESIGN in this job (it has no runner checkout; the dedicated trace-parity job runs them). A
       whole-suite "zero skips" rule is permanently red and would have been disabled within a day.

  So this is an XML parse of the actual document, and the predicate is scoped to the tests the guard
  is actually about: DB-backed tests must have RUN, and none of them may be skipped. Skips OUTSIDE
  those classes are reported but do not fail — they are somebody else's invariant.

Usage: assert-tests-ran.py <path-to.trx>
"""
import sys
import xml.etree.ElementTree as ET

# Test classes whose tests REQUIRE the Postgres fixture ([Collection("postgres")]). Keep in sync with
# the classes carrying that attribute — a class added there and not here is simply unguarded, not broken.
DB_BACKED_CLASSES = (
    "SynthWatch.Api.Tests.IntegrationTests.",
    "SynthWatch.Api.Tests.PreviewCredentialsTests.",
)

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def find(root, path):
    """Look up `path` with the TRX namespace, falling back to no-namespace documents."""
    hit = root.findall(path.replace("PFX", "t:"), NS)
    return hit if hit else root.findall(path.replace("PFX", ""))


def main(argv):
    if len(argv) != 2:
        print("usage: assert-tests-ran.py <path-to.trx>", file=sys.stderr)
        return 2
    path = argv[1]

    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        print(f"::error::could not read/parse the trx at {path}: {exc}")
        return 1

    counters = find(root, ".//PFXResultSummary/PFXCounters")
    if not counters:
        print(f"::error::no <Counters> in {path} — the results file or this parse is broken")
        return 1
    c = counters[0].attrib

    def num(name):
        try:
            return int(c.get(name, "0"))
        except ValueError:
            return 0

    total, executed = num("total"), num("executed")
    passed, failed, not_executed = num("passed"), num("failed"), num("notExecuted")
    print(
        f"suite: total={total} executed={executed} passed={passed} "
        f"failed={failed} notExecuted={not_executed}"
    )

    if total <= 0:
        print("::error::the trx reports total=0 — no tests ran at all")
        return 1

    # Partition every individual result into DB-backed vs not, and ran vs skipped. TRX marks a skipped
    # test with outcome="NotExecuted".
    db_ran, db_skipped, other_skipped = 0, [], []
    for r in find(root, ".//PFXResults/PFXUnitTestResult"):
        name = r.get("testName", "")
        is_db = name.startswith(DB_BACKED_CLASSES)
        if r.get("outcome") == "NotExecuted":
            (db_skipped if is_db else other_skipped).append(name)
        elif is_db:
            db_ran += 1

    print(f"db-backed: ran={db_ran} skipped={len(db_skipped)}")
    if other_skipped:
        # Informational: not this guard's invariant. Named so an unexpected one is still visible.
        print(f"::notice::{len(other_skipped)} non-DB test(s) skipped (not gated here): "
              + ", ".join(sorted(other_skipped)[:10]))

    rc = 0
    if db_skipped:
        print(f"::error::{len(db_skipped)} DB-BACKED test(s) SKIPPED. They skip when the fixture cannot")
        print("::error::reach a Postgres — check the postgres service and DATABASE_URL. A green suite")
        print("::error::that skipped its DB tests is the exact failure this job exists to prevent.")
        for n in sorted(db_skipped)[:10]:
            print(f"::error::  skipped: {n}")
        rc = 1
    if db_ran == 0:
        print("::error::ZERO DB-backed tests executed. Either the fixture is unreachable or the classes")
        print(f"::error::in DB_BACKED_CLASSES were renamed/removed — expected names starting with "
              f"{', '.join(DB_BACKED_CLASSES)}.")
        rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
