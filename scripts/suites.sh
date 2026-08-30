#!/usr/bin/env bash
# Emit a CI matrix from BUILT test assemblies.
#
# A SUITE is a test project: its own fixture, its own containers, its own
# runner. A CLASS is the unit of fan-out within it. The project IS the suite,
# so nothing in the test code declares which one it belongs to.
#
#   suites.sh <suite>=<exe> [<suite>=<exe>...] [--max-parallel SPEC] [--ado]
#
# SPEC caps the legs PER SUITE: "2" for all, or "default=2,e2e=1" to differ.
# That difference is the setting that matters: classes on one leg run in a
# single process and share their collection's fixture, so packing an expensive
# suite avoids rebuilding its containers per leg. Measured on this repo, an e2e
# leg is 94-99% fixture setup.
#
# Class names come from the runner's own structured output:
#
#     <exe> -list classes/json -noColor -noLogo
#
# `-noColor -noLogo` matter: without them the runner prints a banner and wraps
# output in ANSI codes, and an earlier version grepped that text -- the banner
# starts with a letter and contains dots, so it was admitted as a class and
# produced matrix legs called "(64-bit" and "xUnit.net".
set -euo pipefail

pairs=()
cap=""
ado=false

while [ $# -gt 0 ]; do
  case "$1" in
    --max-parallel) cap="$2"; shift 2 ;;
    --ado)          ado=true; shift ;;
    *)              pairs+=("$1"); shift ;;
  esac
done

if [ ${#pairs[@]} -eq 0 ]; then
  echo "usage: suites.sh <suite>=<exe> [...] [--max-parallel SPEC] [--ado]" >&2
  exit 1
fi

# A test project missing from this invocation runs on no runner, which is
# indistinguishable from passing. The solution knows what exists, so ask it
# rather than trusting the caller to have remembered.
declared="$(dotnet sln OrderBook.slnx list \
  | grep -E 'Tests[/\\][^/\\]+\.csproj$' \
  | grep -v 'Tests\.Shared' \
  | sed -E 's#.*[/\\]([^/\\]+)\.csproj#\1#' | sort)"

named="$(printf '%s\n' "${pairs[@]}" | sed -E 's#.*[/\\]([^/\\]+)$#\1#' | sort)"

missing="$(comm -23 <(echo "$declared") <(echo "$named") || true)"
if [ -n "$missing" ]; then
  echo "These test projects are in OrderBook.slnx but no runner would take them:" >&2
  echo "$missing" | sed 's/^/  /' >&2
  echo >&2
  echo "Add them to the suites.sh invocation. Running on no runner is" >&2
  echo "indistinguishable from passing, which is why this is fatal." >&2
  exit 1
fi

suites='{}'
for pair in "${pairs[@]}"; do
  name="${pair%%=*}"
  exe="${pair#*=}"
  [ -x "$exe" ] || { echo "not executable: $exe" >&2; exit 1; }

  classes="$("$exe" -list classes/json -noColor -noLogo)"
  suites="$(jq -n --argjson acc "$suites" --argjson classes "$classes" \
                 --arg name "$name" --arg exe "$exe" '
    $acc + { ($name): { exe: $exe, classes: ($classes | sort) } }
  ')"
done

jq -c --arg cap "$cap" --argjson ado "$ado" '
  # Deal a list round-robin into at most $n groups.
  #
  # Round-robin rather than contiguous chunks, because chunking cannot produce
  # exactly $n groups when $n does not divide the length: 6 classes at a cap of
  # 5 chunked into ceil(6/5)=2 gives THREE legs, silently using three runners
  # where five were asked for. Dealing gives min(length, $n) every time.
  def deal($n): to_entries | group_by(.key % $n) | map(map(.value));

  # "2" or "default=2,e2e=1" -> {"default": 2, "e2e": 1}. Empty means no cap.
  ( $cap
    | if . == "" then {}
      elif test("=") then
        [ split(",")[] | split("=") | { key: .[0], value: (.[1] | tonumber) } ] | from_entries
      else { default: tonumber } end
  ) as $caps

  | with_entries(
      .key as $suite
      | .value.exe as $exe
      | .value |= (
          ($caps[$suite] // $caps["default"] // 0) as $n
          | .classes
          | (if $n > 0 then deal($n) else map([.]) end)
          | to_entries
          | map({
              name:    "\($suite)-\(.key + 1)",
              exe:     $exe,
              # Ready-made arguments, so a pipeline never builds them from an
              # array in YAML. Classes on one leg run in a SINGLE process and
              # therefore share whatever fixture their collection defines.
              args:    ([ .value[] | "-class \"\(.)\"" ] | join(" ")),
              classes: (.value | join(" ")),
            })
        )
    )

  # ADO wants one flat object keyed by a unique leg name.
  | if $ado then
      [ .[][] | { key: .name, value: { EXE: .exe, ARGS: .args, CLASSES: .classes } } ] | from_entries
    else . end
' <<<"$suites"
