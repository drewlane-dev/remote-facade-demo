#!/bin/sh
# Discovers the parallelisable units from the BUILT test assembly, and refuses
# to emit anything if a test class belongs to no suite.
#
# The assembly is the single source of truth. A matrix hand-maintained in YAML
# drifts the moment someone adds a test class, and it drifts SILENTLY: the class
# runs on no agent, every leg stays green, and nothing says coverage shrank.
#
#   suites.sh <exe>          {"integration":["A","B"],"e2e":["C"]}   (GitHub Actions)
#   suites.sh <exe> --ado    {"A":{"SUITE":"integration","CLASS":"A"}, ...}
#
# Two levels: the SUITE is the environment (its fixture, its containers), and
# the CLASS is the unit of fan-out within it. Suites differ in what they cost to
# start, which is why they are separate stages rather than more legs of one.
set -eu

EXE="${1:?usage: suites.sh <path-to-test-executable> [--ado]}"
MODE="${2:-}"
[ -x "$EXE" ] || { echo "not executable: $EXE" >&2; exit 1; }

clean() { tr -d '\033' | sed 's/\[0m//g'; }

# A dotted identifier and nothing else. The runner's banner line also begins
# with a letter and contains dots, and a looser pattern admitted it as a class
# -- producing matrix legs named "(64-bit" and "xUnit.net".
CLASS_RE='^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$'

classes_for() { "$EXE" -list classes -trait "Suite=$1" 2>/dev/null | clean | grep -E "$CLASS_RE" | sort -u || true; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

"$EXE" -list classes 2>/dev/null | clean | grep -E "$CLASS_RE" | sort -u > "$TMP/all" || true
classes_for '*' > "$TMP/covered"

ORPHANS="$(comm -23 "$TMP/all" "$TMP/covered" || true)"
if [ -n "$ORPHANS" ]; then
  echo "These test classes declare no [Trait(Suites.Name, ...)], so no runner would take them:" >&2
  echo "$ORPHANS" | sed 's/^/  /' >&2
  echo "" >&2
  echo "Running on no runner is indistinguishable from passing, which is why this is fatal." >&2
  exit 1
fi

SUITES="$("$EXE" -list traits/json 2>/dev/null | clean | tail -1 \
  | python3 -c 'import json,sys; print("\n".join(json.load(sys.stdin).get("Suite", [])))')"
[ -n "$SUITES" ] || { echo "no Suite traits found; nothing to parallelise" >&2; exit 1; }

for s in $SUITES; do
  printf '%s\t' "$s"
  classes_for "$s" | tr '\n' ' '
  printf '\n'
done > "$TMP/pairs"

# The formatter goes to a FILE. `python3 -` takes its program from stdin, so a
# heredoc there would occupy stdin and sys.stdin would read nothing -- which is
# exactly what happened: valid script, empty output, "{}".
cat > "$TMP/fmt.py" <<'PY'
import json, sys

mode = sys.argv[1] if len(sys.argv) > 1 else ""
suites = {}
for line in sys.stdin:
    if not line.strip():
        continue
    suite, classes = line.rstrip("\n").split("\t", 1)
    suites[suite] = sorted(c for c in classes.split() if c)

if mode == "--ado":
    # ADO needs one flat object, and the leg name must be unique -- so it
    # carries the class rather than the suite.
    print(json.dumps({
        c.split(".")[-1]: {"SUITE": s, "CLASS": c}
        for s, cs in suites.items() for c in cs
    }))
else:
    print(json.dumps(suites))
PY

python3 "$TMP/fmt.py" "$MODE" < "$TMP/pairs"
