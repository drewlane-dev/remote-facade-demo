#!/usr/bin/env python3
"""Fold a run's xUnit XML results into a rolling timings file.

    timings.py timings.json results/*.xml

Two numbers are kept, because a container suite has two costs and only one of
them is per-class:

    classes[<class>]   summed time of that class's tests -- its MARGINAL cost,
                       what adding it to an existing leg costs
    fixtures[<suite>]  what a leg costs before any test runs: containers pulled
                       and started, the graph built

Measured on this repo, the fixture is 94-99% of a leg. Weighting by test time
alone would be balancing the remaining few percent, so both are needed.

Values are an exponentially weighted moving average, so a class that gets
slower is reflected within a few runs instead of being diluted by history.
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

ALPHA = 0.3  # weight of the newest observation


def blend(old, new):
    return new if old is None else (1 - ALPHA) * old + ALPHA * new


def main():
    if len(sys.argv) < 3:
        sys.exit("usage: timings.py <timings.json> <results.xml> [...]")

    path, results = sys.argv[1], sys.argv[2:]

    data = {"classes": {}, "fixtures": {}}
    if os.path.exists(path):
        with open(path) as f:
            data.update(json.load(f))

    for xml in results:
        if not os.path.exists(xml):
            print(f"  skipped (missing): {xml}", file=sys.stderr)
            continue

        root = ET.parse(xml).getroot()

        per_class = {}
        for t in root.iter("test"):
            per_class[t.get("type")] = per_class.get(t.get("type"), 0.0) + float(t.get("time"))

        for cls, seconds in per_class.items():
            data["classes"][cls] = round(blend(data["classes"].get(cls), seconds), 4)

        # The assembly element's own time covers the whole run including
        # fixtures, so the difference is what the environment cost. Attributing
        # it to the SUITE rather than a class is deliberate: it is paid once per
        # leg no matter how many classes ride along.
        suite = os.path.splitext(os.path.basename(xml))[0].rsplit("-", 1)[0]
        total = sum(float(a.get("time", 0)) for a in root.iter("assembly"))
        overhead = max(0.0, total - sum(per_class.values()))
        if overhead:
            data["fixtures"][suite] = round(blend(data["fixtures"].get(suite), overhead), 2)

    with open(path, "w") as f:
        json.dump(data, f, indent=2, sort_keys=True)
        f.write("\n")

    print(f"classes: {len(data['classes'])}  fixtures: {len(data['fixtures'])}  -> {path}")


if __name__ == "__main__":
    main()
