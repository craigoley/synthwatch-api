#!/usr/bin/env bash
# check-no-sigpipe-grep.sh — ban the fail-open SIGPIPE antipattern in shell + workflow `run:` blocks.
#
# THE BUG (five instances org-wide; shellcheck/actionlint miss it): a producer piped into an
# EARLY-CLOSING consumer under `set -o pipefail`:
#     echo/printf "$BIG" | grep -q PATTERN        # grep -q exits on the FIRST match
#     cmd "$BIG"         | head -N                 # head exits after N lines
# When the input is large, the still-writing producer takes SIGPIPE (141) the instant the consumer
# closes the pipe. `pipefail` then makes the WHOLE pipeline exit 141 — even though grep MATCHED. A guard
# built on that exit (`if ! … ; then`, `… || continue`) INVERTS: a match reads as "no match", silently
# flipping a BLOCK→PASS / found→skipped. It is input-size-dependent, so it passes every small-input test
# and fails only in prod on a large input.
#
# THE FIX: a here-string (no piped writer to kill) + explicit if/then — NOT `|| true` (which hides it):
#     if ! grep -q PATTERN <<< "$BIG"; then …           # or:  grep -q P <<< "$(cmd)"
#
# This scanner flags `| grep …-q…`, `| grep -m N`, and `| head` in tracked *.sh and workflow YAML.
# Here-strings (`<<<`) are safe. A reviewed-safe line may opt out with a trailing `# sigpipe-ok` comment.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Tracked shell scripts + workflow files only (deterministic in CI). Portable array read (no bash-4 mapfile).
FILES=()
while IFS= read -r _f; do FILES+=("$_f"); done < <(git ls-files '*.sh' '.github/workflows/*.yml' '.github/workflows/*.yaml' 2>/dev/null)

# A real pipe (NOT `||`) into an early-closing consumer. The leading [^|] rules out the 2nd bar of `||`.
PATTERN='[^|]\|[[:space:]]*(grep[[:space:]]+-[A-Za-z]*q[A-Za-z]*|grep[[:space:]]+-[A-Za-z]*m[[:space:]]*[0-9]|head)([[:space:]]|$)'

[ ${#FILES[@]} -eq 0 ] && { echo "no tracked shell/workflow files to scan"; exit 0; }

found=0
for f in "${FILES[@]}"; do
  [ -f "$f" ] || continue
  while IFS=: read -r lineno line; do
    # skip full-line comments (leading # after optional whitespace), here-strings, and opt-outs
    case "$(printf '%s' "$line" | sed 's/^[[:space:]]*//')" in '#'*) continue;; esac
    case "$line" in *'<<<'*) continue;; esac
    case "$line" in *'# sigpipe-ok'*) continue;; esac
    found=1
    echo "::error file=$f,line=$lineno::SIGPIPE-grep antipattern — pipe into an early-closing consumer under pipefail. Use a here-string: grep -q P <<< \"\$X\" (see scripts/check-no-sigpipe-grep.sh)."
    echo "  $f:$lineno: ${line#"${line%%[![:space:]]*}"}"
  done < <(grep -nE "$PATTERN" "$f" 2>/dev/null)
done

if [ "$found" -ne 0 ]; then
  echo "::error::Found the fail-open SIGPIPE-grep antipattern above. Fix with a here-string + if/then, or annotate a proven-safe line with '# sigpipe-ok'."
  exit 1
fi
echo "✅ no fail-open SIGPIPE-grep antipattern in $((${#FILES[@]})) tracked shell/workflow files."
