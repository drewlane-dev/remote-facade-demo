#!/usr/bin/env python3
"""Discover the parallelisable suites from the BUILT test assembly.

Nothing here reads source code. Every fact comes from the test runner's own
structured output:

    <exe> -list traits/json  -noColor -noLogo          which suites exist
    <exe> -list classes/json -noColor -noLogo          which classes exist
    <exe> -list classes/json -noColor -noLogo -trait X which belong to a suite

`-noColor -noLogo` matters: without them the runner prints a banner and wraps
its output in ANSI codes, and an earlier version of this script grepped the
text instead. The banner begins with a letter and contains dots, so it was
admitted as a class and produced matrix legs named "(64-bit" and "xUnit.net".
Asking for JSON removes the guessing rather than improving it.

    suites.py <exe>          {"integration": ["A", "B"], "e2e": ["C"]}
    suites.py <exe> --ado    {"A": {"SUITE": "integration", "CLASS": "A"}, ...}

Two levels: the SUITE is the environment (its fixture, its containers), and the
CLASS is the unit of fan-out within it.
"""
import json
import subprocess
import sys


def listing(exe, what, *extra):
    """One `-list <what>/json` call, parsed. Raises if the runner fails."""
    out = subprocess.run(
        [exe, "-list", f"{what}/json", "-noColor", "-noLogo", *extra],
        capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: suites.py <path-to-test-executable> [--ado]")

    exe, mode = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "")

    names = listing(exe, "traits").get("Suite", [])
    if not names:
        sys.exit("no Suite traits found; nothing to parallelise")

    suites = {s: sorted(listing(exe, "classes", "-trait", f"Suite={s}")) for s in sorted(names)}

    # A class in no suite would run on no runner, which is indistinguishable
    # from passing. Fatal, rather than a warning nobody reads.
    covered = {c for cs in suites.values() for c in cs}
    orphans = sorted(set(listing(exe, "classes")) - covered)
    if orphans:
        print("These test classes declare no [Trait(Suites.Name, ...)], "
              "so no runner would take them:", file=sys.stderr)
        for o in orphans:
            print(f"  {o}", file=sys.stderr)
        print("\nRunning on no runner is indistinguishable from passing, "
              "which is why this is fatal.", file=sys.stderr)
        sys.exit(1)

    if mode == "--ado":
        # ADO wants one flat object, and the leg name must be unique -- so it
        # carries the class rather than the suite.
        print(json.dumps({
            c.rsplit(".", 1)[-1]: {"SUITE": s, "CLASS": c}
            for s, cs in suites.items() for c in cs
        }))
    else:
        print(json.dumps(suites))


if __name__ == "__main__":
    main()
