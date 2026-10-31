"""Fail when a production method's CRAP score is above the limit and no listed exception pins it.

CRAP = complexity^2 * (1 - coverage)^3 + complexity, per method, where coverage is the line coverage
of that method in the Cobertura report. Complexity estimates cyclomatic complexity: Microsoft.CodeCoverage
writes the number of IL branch outcomes in the complexity attribute, and one decision has two outcomes,
so complexity = outcomes // 2 + 1. A branch the compiler adds counts too (a pattern in an if adds one),
and an IL switch counts low (13 outcomes count as 6 decisions, not 12). Only files under --include
(default src/) count; test projects and files under obj/ (generated code) are skipped.

An exception pins the highest score and the number of bodies above the limit under its key, so it records a
baseline. A worse score, an extra body, and an entry for a method that no longer exists fail. A better score,
a missing body, and a key with no body above the limit print a warning and pass.

Exit codes: 0 pass, 1 a method above the limit, a listed key worse than its pin, or a listed method that is
gone, 2 bad input (no coverage, a source project missing from the coverage, an exception without a reason or
a pin).
"""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

LIMIT = 10.0
# Listed scores carry four decimals. That survives floating-point differences between platforms, and one
# line of coverage on a large method near the limit still moves a score by more than this.
SCORE_PRECISION = 0.0001
ROOT = Path(__file__).resolve().parents[1]

# Compiler-generated names carry ordinals that change whenever a method is added, so a body is named
# after the source method it came from: <M>d__4 (async/iterator), <M>b__0 (lambda), <M>g__Local|7_0.
# A state machine can be generic (<M>d__4<T>) and can belong to an async lambda or local function
# (<<M>b__8_0>d, <<M>g__Local|1>d). A local function's closure is a ref parameter in its signature.
STATE_MACHINE = re.compile(r"^<(?P<method>.+)>d(__\d+)?(<.*>)?$")
LAMBDA = re.compile(r"^<(?P<method>.*)>b__[\d_]+$")
LOCAL_FUNCTION = re.compile(r"^<(?P<method>.+?)>g__(?P<local>[^|]+)\|[\d_]+$")
CLOSURE = re.compile(r"^<>c(__DisplayClass)?[\d_]*(<.*>)?$")
CLOSURE_PARAMETER = re.compile(r"(, )?ref [^,()]*<>c__DisplayClass[^,()]*")


def crap(complexity, coverage):
    return complexity ** 2 * (1 - coverage) ** 3 + complexity


def cyclomatic(outcomes):
    return outcomes // 2 + 1


def source_name(class_name, method_name, signature):
    """Returns the stable name of the source method a Cobertura method entry belongs to."""
    parts = split_type_name(class_name)
    machine = STATE_MACHINE.match(parts[-1])
    if machine and method_name == "MoveNext":
        # The async or iterator body: its signature is MoveNext's, so leave it out.
        return ".".join(without_closures(parts[:-1]) + [lambda_name(machine.group("method"))])
    return ".".join(without_closures(parts) + [lambda_name(method_name)]) + CLOSURE_PARAMETER.sub("", signature)


def without_closures(parts):
    """A closure class adds no name of its own; its lambdas carry the method name."""
    parts = list(parts)
    while parts and CLOSURE.match(parts[-1]):
        parts.pop()
    return parts


def split_type_name(name):
    """Splits a type name at its dots, but not at dots inside <...> (explicit interface members)."""
    parts, depth, start = [], 0, 0
    for index, character in enumerate(name):
        depth += {"<": 1, ">": -1}.get(character, 0)
        if character == "." and depth == 0:
            parts.append(name[start:index])
            start = index + 1
    return parts + [name[start:]]


def lambda_name(name):
    local = LOCAL_FUNCTION.match(name)
    if local:
        return f"{lambda_name(local.group('method'))}.{local.group('local')}"
    anonymous = LAMBDA.match(name)
    if anonymous:
        return f"{lambda_name(anonymous.group('method'))}.<lambda>"
    return name


def measured(filename, root, include):
    path = Path(filename)
    try:
        relative = path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return False
    return any(relative.startswith(prefix.rstrip("/") + "/") for prefix in include) and "/obj/" not in relative


def methods(paths, root, include):
    found = []
    for path in paths:
        for element in ET.parse(path).getroot().iter("class"):
            if not measured(element.get("filename", ""), root, include):
                continue
            for entry in element.iter("method"):
                complexity = cyclomatic(int(entry.get("complexity")))
                coverage = float(entry.get("line-rate"))
                found.append({"method": source_name(element.get("name"), entry.get("name"), entry.get("signature", "")),
                              "file": Path(element.get("filename")).resolve().relative_to(root.resolve()).as_posix(),
                              "complexity": complexity, "lineCoverage": round(coverage, 4),
                              "score": crap(complexity, coverage)})
    return found


def load_exceptions(path):
    entries = json.loads(Path(path).read_text(encoding="utf-8"))["exceptions"]
    listed = {}
    for entry in entries:
        name = entry.get("method", "")
        if not str(entry.get("reason", "")).strip():
            raise ValueError(f"CRAP exception {name!r} needs a reason")
        if not isinstance(entry.get("score"), (int, float)) or not isinstance(entry.get("bodies"), int):
            raise ValueError(f"CRAP exception {name!r} needs a score and a bodies count")
        if name in listed:
            raise ValueError(f"CRAP exception {name!r} is listed twice")
        listed[name] = entry
    return listed


def evaluate(found, exceptions, limit):
    above = [entry for entry in found if entry["score"] > limit]
    violations = [entry for entry in above if entry["method"] not in exceptions]
    excepted = [entry for entry in above if entry["method"] in exceptions]
    measured_names = {entry["method"] for entry in found}
    above_names = {entry["method"] for entry in above}
    unused = sorted(name for name in exceptions if name not in measured_names)
    worse, better = out_of_date(excepted, exceptions)
    better += [f"{name}: no body above the limit, recorded {exceptions[name]['score']}"
               for name in sorted(exceptions) if name in measured_names and name not in above_names]
    return violations, excepted, unused, worse, better


def out_of_date(excepted, exceptions):
    """Compares what each listed key measures with its pin, and returns the keys that got worse and those
    that got better. Only worse fails: a pin bounds the room a better result leaves, so a later change
    cannot pass the recorded baseline, and a CI round per improvement would not buy anything."""
    measured_scores = {}
    for entry in excepted:
        measured_scores.setdefault(entry["method"], []).append(entry["score"])
    worse, better = [], []
    for name, scores in sorted(measured_scores.items()):
        pin = exceptions[name]
        change = max(scores) - pin["score"]
        if abs(change) > SCORE_PRECISION:
            (worse if change > 0 else better).append(f"{name}: score {max(scores):.4f}, recorded {pin['score']}")
        if len(scores) != pin["bodies"]:
            (worse if len(scores) > pin["bodies"] else better).append(
                f"{name}: {len(scores)} bodies above the limit, recorded {pin['bodies']}")
    return worse, better


def unmeasured_projects(found, root, include):
    """Source projects with no method in the coverage: losing a whole assembly must not pass."""
    projects = sorted(path.parent.relative_to(root).as_posix()
                      for prefix in include for path in (root / prefix).glob("*/*.csproj"))
    return [project for project in projects if not any(entry["file"].startswith(project + "/") for entry in found)]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("coverage", type=Path, nargs="+", help="Cobertura files")
    parser.add_argument("--exceptions", type=Path, default=ROOT / "build/crap-exceptions.json")
    parser.add_argument("--limit", type=float, default=LIMIT)
    parser.add_argument("--root", type=Path, default=ROOT, help="Folder the source paths are relative to")
    parser.add_argument("--include", action="append", help="Measured folder under --root (default src)")
    parser.add_argument("--report", type=Path, help="Write the result as JSON")
    args = parser.parse_args(argv)
    try:
        exceptions = load_exceptions(args.exceptions)
        include = args.include or ["src"]
        found = methods(args.coverage, args.root, include)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        print(f"crap-gate: {error}", file=sys.stderr)
        return 2
    if not found:
        print("crap-gate: the coverage has no production methods; was coverage collected?", file=sys.stderr)
        return 2
    missing = unmeasured_projects(found, args.root, include)
    if missing:
        print(f"crap-gate: the coverage has no methods of {', '.join(missing)}", file=sys.stderr)
        return 2
    violations, excepted, unused, stale, improved = evaluate(found, exceptions, args.limit)
    if args.report:
        args.report.write_text(json.dumps({"limit": args.limit, "methods": len(found), "violations": violations,
                                           "excepted": excepted, "unusedExceptions": unused,
                                           "outOfDateExceptions": stale, "improvedExceptions": improved}, indent=2),
                               encoding="utf-8")
    for entry in sorted(violations, key=lambda item: -item["score"]):
        print(f"CRAP {entry['score']:.4f} > {args.limit:g}: {entry['method']} ({entry['file']}, "
              f"complexity {entry['complexity']}, line coverage {entry['lineCoverage']:.0%})")
    for name in unused:
        print(f"CRAP exception not used (remove it): {name}")
    for line in stale:
        print(f"CRAP exception out of date: {line}")
    for line in improved:
        print(f"CRAP exception better than recorded (lower the pin): {line}")
    print(f"crap-gate: {len(found)} methods, {len(violations)} above {args.limit:g}, {len(excepted)} bodies "
          f"under {len(exceptions)} listed exceptions, {len(unused) + len(stale)} exception problems, "
          f"{len(improved)} better than recorded")
    return 1 if violations or unused or stale else 0


if __name__ == "__main__":
    sys.exit(main())
