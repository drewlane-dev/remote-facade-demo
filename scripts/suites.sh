#!/bin/sh
# Discovers the parallelisable suites from the BUILT test assembly and emits an
# Azure DevOps matrix, then refuses to proceed if any test class belongs to no
# suite.
#
# The assembly is the single source of truth. A matrix hand-maintained in YAML
# drifts the moment someone adds a test class, and it drifts SILENTLY: the class
# runs on no agent, every leg stays green, and nothing says the coverage shrank.
#
# Usage: scripts/suites.sh <path-to-test-executable>
set -eu

EXE="${1:?usage: suites.sh <path-to-test-executable>}"
[ -x "$EXE" ] || { echo "not executable: $EXE" >&2; exit 1; }

# Strip the ANSI reset the runner emits after its output; it corrupts the JSON.
clean() { tr -d '\033' | sed 's/\[0m//g'; }

# Temp files rather than process substitution: this runs under /bin/sh, where
# <(...) is a bash-ism and fails with a syntax error on dash.
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

"$EXE" -list classes 2>/dev/null | clean | grep -E '^[A-Za-z].*\.' | sort -u > "$TMP/all" || true
"$EXE" -list classes -trait "Suite=*" 2>/dev/null | clean | grep -E '^[A-Za-z].*\.' | sort -u > "$TMP/covered" || true

ORPHANS="$(comm -23 "$TMP/all" "$TMP/covered" || true)"

if [ -n "$ORPHANS" ]; then
  echo "These test classes declare no [Trait(Suites.Name, ...)], so no agent would run them:" >&2
  echo "$ORPHANS" | sed 's/^/  /' >&2
  echo "" >&2
  echo "Add a suite trait, or add the class to an existing suite. Running on no" >&2
  echo "agent is indistinguishable from passing, which is why this is fatal." >&2
  exit 1
fi

# {"domain":{"SUITE":"domain"},"web-ui":{"SUITE":"web-ui"}} -- the shape ADO's
# `strategy: matrix: $[ ... ]` expects.
"$EXE" -list traits/json 2>/dev/null | clean | tail -1 | python3 -c '
import json, sys
traits = json.load(sys.stdin)
suites = traits.get("Suite", [])
if not suites:
    sys.stderr.write("no Suite traits found; nothing to parallelise\n")
    sys.exit(1)
print(json.dumps({s: {"SUITE": s} for s in sorted(suites)}))
'
