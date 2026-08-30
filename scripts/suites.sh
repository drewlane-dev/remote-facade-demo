#!/usr/bin/env bash
# Emit a CI matrix from the solution's test projects.
#
# A SUITE is a test project, identified by its NAME:
#
#     *.IntegrationTests  ->  integration
#     *.E2ETests          ->  e2e
#
# so nothing declares which suite it belongs to -- not the test code, not the
# pipeline. Adding a test project to OrderBook.slnx is all it takes to get a
# runner, and a Tests project matching neither pattern is fatal rather than
# silently skipped.
#
#   suites.sh [--configuration Release] [--max-parallel SPEC] [--ado]
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

solution="OrderBook.slnx"
configuration="Release"
cap=""
ado=false

while [ $# -gt 0 ]; do
  case "$1" in
    --configuration) configuration="$2"; shift 2 ;;
    --max-parallel)  cap="$2"; shift 2 ;;
    --ado)           ado=true; shift ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done

suite_of() {
  case "$1" in
    *.IntegrationTests) echo integration ;;
    *.E2ETests)         echo e2e ;;
    *)                  echo "" ;;
  esac
}

suites='{}'
unclassified=""

# Every project the solution knows about, so a new one is picked up by being
# added to the solution rather than by also being remembered here.
while IFS= read -r project; do
  name="$(basename "${project%.csproj}")"

  case "$name" in
    *Tests) ;;          # a test project
    *) continue ;;
  esac
  case "$name" in
    *Tests.Shared|*.TestHelpers) continue ;;   # support libraries, not suites
  esac

  suite="$(suite_of "$name")"
  if [ -z "$suite" ]; then
    unclassified="$unclassified  $name"$'\n'
    continue
  fi

  # Glob the framework folder rather than naming it: a TFM bump should not need
  # an edit here.
  exe="$(ls -d "$(dirname "$project")/bin/$configuration"/*/"$name" 2>/dev/null | head -1 || true)"
  if [ ! -x "${exe:-}" ]; then
    echo "$name is in $solution but has no $configuration build at" >&2
    echo "  $(dirname "$project")/bin/$configuration/*/$name" >&2
    echo "Build the solution before asking for a matrix." >&2
    exit 1
  fi

  classes="$("$exe" -list classes/json -noColor -noLogo)"
  suites="$(jq -n --argjson acc "$suites" --argjson classes "$classes" \
                 --arg suite "$suite" --arg exe "$exe" '
    $acc + { ($suite): { exe: $exe, classes: ($classes | sort) } }
  ')"
done <<<"$(dotnet sln "$solution" list | grep -E '\.csproj$' | tr -d '\r')"

# A Tests project matching neither pattern would run on no runner, which is
# indistinguishable from passing.
if [ -n "$unclassified" ]; then
  echo "These test projects match neither *.IntegrationTests nor *.E2ETests," >&2
  echo "so no runner would take them:" >&2
  printf '%s' "$unclassified" >&2
  echo "Rename them, or teach suite_of about the new suite." >&2
  exit 1
fi

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
