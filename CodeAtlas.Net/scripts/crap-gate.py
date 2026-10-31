"""Fail when a production method's CRAP score is above the limit and it is not a listed exception.

CRAP = complexity^2 * (1 - coverage)^3 + complexity, per method, where complexity is the cyclomatic
complexity and coverage the line coverage of that method in the Cobertura report. ReportGenerator uses
the same formula and inputs for its risk hotspots. Only files under --include (default src/) count;
test projects and files under obj/ (generated code) are skipped.

Exit codes: 0 pass, 1 a method is above the limit, 2 bad input (no coverage, an exception without a reason).
"""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

LIMIT = 10.0
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
                complexity = int(entry.get("complexity"))
                coverage = float(entry.get("line-rate"))
                found.append({"method": source_name(element.get("name"), entry.get("name"), entry.get("signature", "")),
                              "file": Path(element.get("filename")).resolve().relative_to(root.resolve()).as_posix(),
                              "complexity": complexity, "lineCoverage": round(coverage, 4),
                              "score": round(crap(complexity, coverage), 2)})
    return found


def load_exceptions(path):
    entries = json.loads(Path(path).read_text(encoding="utf-8"))["exceptions"]
    listed = {}
    for entry in entries:
        name = entry.get("method", "")
        if not str(entry.get("reason", "")).strip():
            raise ValueError(f"CRAP exception {name!r} needs a reason")
        if name in listed:
            raise ValueError(f"CRAP exception {name!r} is listed twice")
        listed[name] = entry
    return listed


def evaluate(found, exceptions, limit):
    above = [entry for entry in found if entry["score"] > limit]
    violations = [entry for entry in above if entry["method"] not in exceptions]
    excepted = [entry for entry in above if entry["method"] in exceptions]
    measured_names = {entry["method"] for entry in above}
    unused = sorted(name for name in exceptions if name not in measured_names)
    return violations, excepted, unused


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
        found = methods(args.coverage, args.root, args.include or ["src"])
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        print(f"crap-gate: {error}", file=sys.stderr)
        return 2
    if not found:
        print("crap-gate: the coverage has no production methods; was coverage collected?", file=sys.stderr)
        return 2
    violations, excepted, unused = evaluate(found, exceptions, args.limit)
    if args.report:
        args.report.write_text(json.dumps({"limit": args.limit, "methods": len(found), "violations": violations,
                                           "excepted": excepted, "unusedExceptions": unused}, indent=2),
                               encoding="utf-8")
    for entry in sorted(violations, key=lambda item: -item["score"]):
        print(f"CRAP {entry['score']} > {args.limit:g}: {entry['method']} ({entry['file']}, "
              f"complexity {entry['complexity']}, line coverage {entry['lineCoverage']:.0%})")
    # An exception whose method is now within the limit (or gone) only needs removing; it does not fail.
    for name in unused:
        print(f"CRAP exception no longer needed: {name}")
    print(f"crap-gate: {len(found)} methods, {len(violations)} above {args.limit:g}, {len(excepted)} listed exceptions")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
