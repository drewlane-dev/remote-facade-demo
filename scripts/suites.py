#!/usr/bin/env python3
"""Discover the parallelisable suites from the BUILT test assembly, and pack
their classes into a bounded number of legs.

Nothing here reads source code. Every fact comes from the test runner's own
structured output:

    <exe> -list traits/json -noColor -noLogo    which suites exist
    <exe> -list full/json   -noColor -noLogo    every test, its class and traits

`-noColor -noLogo` matters: without them the runner prints a banner and wraps
output in ANSI codes, and an earlier version grepped the text instead. The
banner begins with a letter and contains dots, so it was admitted as a class
and produced matrix legs named "(64-bit" and "xUnit.net". Asking for JSON
removes the guessing rather than improving it.

Usage:
    suites.py <exe> [--max-parallel SPEC] [--timings FILE] [--ado]

    SPEC is "default=3" or "default=3,e2e=1" -- a cap on legs PER SUITE.
    Omitted, every class gets its own leg.

With --timings, classes are weighted by their recorded runtime instead of by
test count, and the predicted cost of each leg is reported. That report is the
point as much as the balancing: a leg costs `fixture setup + the test time of
its classes`, and measured here the fixture is 94-99% of it -- so splitting a
suite often buys almost nothing for double the runners, and the numbers say so
rather than leaving it to be guessed.

Packing matters more than it looks. Classes on one leg run in a single process,
so classes sharing a collection share ONE fixture -- which is the cost that
dominates a container suite. Splitting a collection across legs rebuilds its
containers per leg: measured on this repo, two e2e legs took 95s and 91s where
almost all of it was setup, against ~95s for both together.
"""
import json
import os
import subprocess
import sys
from collections import defaultdict


def listing(exe, what, *extra):
    """One `-list <what>/json` call, parsed. Raises if the runner fails."""
    out = subprocess.run(
        [exe, "-list", f"{what}/json", "-noColor", "-noLogo", *extra],
        capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def parse_caps(spec):
    """'default=3,e2e=1' -> {'default': 3, 'e2e': 1}."""
    caps = {}
    for part in filter(None, (p.strip() for p in spec.split(","))):
        key, _, value = part.partition("=")
        if not value:
            sys.exit(f"--max-parallel expects key=value pairs, got '{part}'")
        caps[key.strip()] = int(value)
    return caps


def pack(weights, cap):
    """Greedy longest-first bin packing: heaviest classes placed first, each into
    the lightest leg so far. Balances better than round-robin when class costs
    differ, which they usually do."""
    if cap is None or cap >= len(weights):
        return [[c] for c in sorted(weights)]

    legs = [[] for _ in range(max(1, cap))]
    load = [0.0] * len(legs)

    for name, w in sorted(weights.items(), key=lambda kv: (-kv[1], kv[0])):
        i = load.index(min(load))
        legs[i].append(name)
        load[i] += w

    return [sorted(leg) for leg in legs if leg]


def report(suite, legs, weights, fixture):
    """What the split actually buys, on stderr so it never pollutes the matrix.

    A leg costs `fixture + the test time of its classes`. The fixture is paid
    once per leg regardless, so adding legs shortens only the test-time term --
    and when that term is small, more runners buy almost nothing."""
    if fixture is None:
        print(f"  {suite}: {len(legs)} leg(s) — no timing data, balanced by test count",
              file=sys.stderr)
        return

    costs = [fixture + sum(weights.get(c, 0.0) for c in leg) for leg in legs]
    total_tests = sum(weights.values())
    one_leg = fixture + total_tests

    print(f"  {suite}: {len(legs)} leg(s), slowest ~{max(costs):.1f}s "
          f"(fixture {fixture:.1f}s + tests)", file=sys.stderr)

    if len(legs) > 1:
        saved = one_leg - max(costs)
        extra = len(legs) - 1
        verdict = "worth it" if saved > fixture * 0.25 else "probably not worth it"
        print(f"      vs 1 leg at ~{one_leg:.1f}s: saves {saved:.1f}s for {extra} "
              f"extra runner(s) — {verdict}", file=sys.stderr)


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit("usage: suites.py <path-to-test-executable> [--max-parallel SPEC] [--ado]")

    exe = args[0]
    ado = "--ado" in args
    caps = {}
    if "--max-parallel" in args:
        caps = parse_caps(args[args.index("--max-parallel") + 1])

    timings = {"classes": {}, "fixtures": {}}
    if "--timings" in args:
        path = args[args.index("--timings") + 1]
        if os.path.exists(path):
            with open(path) as f:
                timings.update(json.load(f))
        else:
            print(f"  no timings at {path}; falling back to test counts", file=sys.stderr)

    suite_names = listing(exe, "traits").get("Suite", [])
    if not suite_names:
        sys.exit("no Suite traits found; nothing to parallelise")

    # One call for everything: each entry carries its class and its traits, so
    # suite membership and test counts come from the same source of truth.
    tests = listing(exe, "full")
    by_suite = defaultdict(lambda: defaultdict(int))
    orphans = set()

    for t in tests:
        suites_of = t.get("Traits", {}).get("Suite") or []
        if not suites_of:
            orphans.add(t["Class"])
            continue
        for s in suites_of:
            by_suite[s][t["Class"]] += 1

    # A class in no suite would run on no runner, which is indistinguishable
    # from passing. Fatal, rather than a warning nobody reads.
    if orphans:
        print("These test classes declare no [Trait(Suites.Name, ...)], "
              "so no runner would take them:", file=sys.stderr)
        for o in sorted(orphans):
            print(f"  {o}", file=sys.stderr)
        print("\nRunning on no runner is indistinguishable from passing, "
              "which is why this is fatal.", file=sys.stderr)
        sys.exit(1)

    result = {}
    for suite in sorted(suite_names):
        cap = caps.get(suite, caps.get("default"))

        # Recorded runtime where we have it, test count where we do not. A new
        # class with no history gets the MEDIAN of the known ones rather than
        # zero -- zero would make every new class look free and pile them all
        # onto one leg.
        known = [timings["classes"][c] for c in by_suite[suite] if c in timings["classes"]]
        default_weight = sorted(known)[len(known) // 2] if known else None

        weights = {
            c: timings["classes"].get(c, default_weight if default_weight is not None else count)
            for c, count in by_suite[suite].items()
        }

        legs = pack(weights, cap)
        report(suite, legs, weights, timings["fixtures"].get(suite))
        result[suite] = [
            {
                "name": f"{suite}-{i + 1}",
                # Ready-made arguments, so a pipeline never has to build them
                # from an array in YAML.
                "args": " ".join(f'-class "{c}"' for c in leg),
                "classes": " ".join(leg),
            }
            for i, leg in enumerate(legs)
        ]

    if ado:
        # ADO wants one flat object keyed by a unique leg name.
        print(json.dumps({
            leg["name"]: {"SUITE": s, "ARGS": leg["args"], "CLASSES": leg["classes"]}
            for s, legs in result.items() for leg in legs
        }))
    else:
        print(json.dumps(result))


if __name__ == "__main__":
    main()
