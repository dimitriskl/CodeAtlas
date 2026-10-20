#!/usr/bin/env python3
"""Compare complete analysis contents, preserving duplicates and ignoring run metadata/order.

Exit 0: equivalent; 1: analysis differs; 2: invalid input. Optional --output retains
the full canonical baseline/candidate and complete differences for later regressions.
Input fingerprints are reported separately: equivalence does not imply unchanged inputs.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def encoded(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def normalize(value):
    if isinstance(value, dict):
        return {key: normalize(item) for key, item in sorted(value.items())}
    if isinstance(value, list):
        return sorted((normalize(item) for item in value), key=encoded)
    return value


def canonical(index):
    validate(index)
    # Do not normalize paths/case or discard project/definition/kind: those are semantics.
    for name in ("projects", "types", "methods", "references"):
        if name not in index:
            raise ValueError(f"Missing analysis field: {name}")
    fields = ["projects", "types", "methods", "references"]
    if index["schemaVersion"] == 2:
        fields.extend(("symbols", "documentationCoverage"))
    return normalize({key: index[key] for key in fields})


def validate(index):
    """Reject malformed schema-1 analysis, rather than comparing missing/invalid data."""
    def require(condition, message):
        if not condition:
            raise ValueError(message)

    def strings(value):
        return isinstance(value, list) and all(isinstance(item, str) for item in value)

    def record(value, fields, label):
        require(isinstance(value, dict), f"{label} must be an object")
        for field in fields:
            require(isinstance(value.get(field), str) and bool(value[field]), f"{label}.{field} must be a nonempty string")

    def located(value, fields, label):
        record(value, fields, label)
        location = value.get("location")
        record(location, ("file",), f"{label}.location")
        for field in ("line", "column"):
            require(type(location.get(field)) is int and location[field] > 0,
                    f"{label}.location.{field} must be a positive integer")

    require(isinstance(index, dict), "Index must be an object")
    require(type(index.get("schemaVersion")) is int and index["schemaVersion"] in (1, 2),
            "Unsupported or missing schemaVersion (expected 1 or 2)")
    for section in ("projects", "types", "methods"):
        require(isinstance(index.get(section), list), f"{section} must be an array")
    require(isinstance(index.get("references"), dict), "references must be an object")
    for project in index["projects"]:
        record(project, ("path", "name"), "project")
        require(strings(project.get("dependencies")) and strings(project.get("files")), "Project dependencies/files must be string arrays")
    for symbol in index["types"]:
        located(symbol, ("id", "name", "kind", "project"), "type")
    for method in index["methods"]:
        located(method, ("id", "name", "signature", "project"), "method")
    method_ids = [method["id"] for method in index["methods"]]
    require(len(method_ids) == len(set(method_ids)), "Duplicate method IDs")
    require(set(index["references"]) == set(method_ids), "Reference keys must match method IDs")
    for method_id, references in index["references"].items():
        require(isinstance(references, list), f"References for {method_id} must be an array")
        for reference in references:
            located(reference, ("definitionId", "project", "kind"), "reference")
            require(reference["kind"] in ("invocation", "reference"), "Unknown reference kind")
    if index["schemaVersion"] == 2:
        require(isinstance(index.get("symbols"), list), "symbols must be an array")
        require(isinstance(index.get("documentationCoverage"), dict), "documentationCoverage must be an object")
        require(index["documentationCoverage"].get("version") == 1, "Unsupported documentation coverage")
        symbol_ids = []
        for symbol in index["symbols"]:
            located(symbol, ("id", "name", "kind", "signature", "project"), "symbol")
            symbol_ids.append(symbol["id"])
            require(isinstance(symbol.get("documentation"), dict), "symbol.documentation must be an object")
        require(len(symbol_ids) == len(set(symbol_ids)), "Duplicate symbol IDs")
    if "fingerprints" in index:
        require(isinstance(index["fingerprints"], dict) and
                all(isinstance(path, str) and isinstance(digest, str) for path, digest in index["fingerprints"].items()),
                "fingerprints must be a string-to-string object")


def differences(before, after):
    result = {}
    for section in ("projects", "types", "methods", "references", "symbols", "documentationCoverage"):
        if section not in before and section not in after:
            continue
        if section == "documentationCoverage":
            if encoded(before.get(section)) != encoded(after.get(section)):
                result[section] = {"before": before.get(section), "after": after.get(section)}
            continue
        if section == "references":
            old = [{"methodId": key, "reference": ref} for key, refs in before[section].items() for ref in refs]
            new = [{"methodId": key, "reference": ref} for key, refs in after[section].items() for ref in refs]
            old.extend({"methodId": key, "empty": True} for key, refs in before[section].items() if not refs)
            new.extend({"methodId": key, "empty": True} for key, refs in after[section].items() if not refs)
        else:
            old, new = before[section], after[section]
        old_counts, new_counts = Counter(map(encoded, old)), Counter(map(encoded, new))
        removed, added = old_counts - new_counts, new_counts - old_counts
        if removed or added:
            result[section] = {
                "removed": [{"value": json.loads(value), "count": count} for value, count in sorted(removed.items())],
                "added": [{"value": json.loads(value), "count": count} for value, count in sorted(added.items())],
            }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--output", type=Path, help="New directory for complete retained canonical data")
    args = parser.parse_args()
    try:
        baseline = json.loads(args.baseline.read_text(encoding="utf-8-sig"))
        candidate = json.loads(args.candidate.read_text(encoding="utf-8-sig"))
        before, after = canonical(baseline), canonical(candidate)
        delta = differences(before, after)
        report = {
            "equivalent": before == after,
            "sameInputFingerprints": (baseline["fingerprints"] == candidate["fingerprints"]
                                      if "fingerprints" in baseline and "fingerprints" in candidate else None),
            "baselineIndexId": baseline.get("indexId"),
            "candidateIndexId": candidate.get("indexId"),
            "baselineSha256": hashlib.sha256(encoded(before).encode()).hexdigest(),
            "candidateSha256": hashlib.sha256(encoded(after).encode()).hexdigest(),
            "changedSections": list(delta),
        }
        if args.output:
            args.output.mkdir(parents=True, exist_ok=False)
            for filename, value in (("baseline.canonical.json", before), ("candidate.canonical.json", after),
                                    ("differences.json", delta), ("comparison.json", report)):
                (args.output / filename).write_text(encoded(value) + "\n", encoding="utf-8")
            report["artifacts"] = str(args.output.resolve())
        print(json.dumps(report, indent=2))
        return 0 if report["equivalent"] else 1
    except (OSError, ValueError, TypeError, KeyError) as error:
        parser.exit(2, f"Cannot compare indexes: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
