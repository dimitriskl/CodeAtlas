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
import os
from pathlib import Path, PureWindowsPath
import re


BASE_SECTIONS = ("projects", "types", "methods", "references")
CATALOG_SECTIONS = ("symbols", "documentationCoverage", "structuralCatalog", "duplicates")
ARTIFACTS = {"index.json", "overview.md", "warnings.txt", "timings.json"}
# These collections are sets/multisets. Unknown arrays remain ordered: token
# sequences, argument ordinals and provenance paths must never be sorted away.
UNORDERED_COLLECTIONS = {"projects", "types", "methods", "symbols", "dependencies", "files", "locations",
                         "comments", "sections", "inheritance", "issues", "facts", "groups", "memberIds",
                         "symbolKinds", "factKinds", "exclusions"}


def encoded(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def normalize(value, field=None):
    if isinstance(value, dict):
        return {key: normalize(item, key) for key, item in sorted(value.items())}
    if isinstance(value, list):
        values = [normalize(item) for item in value]
        return sorted(values, key=encoded) if field in UNORDERED_COLLECTIONS else values
    return value


def semantic_sections(index):
    return list(BASE_SECTIONS) + (list(CATALOG_SECTIONS[:2]) if index["schemaVersion"] >= 2 else []) + (
        list(CATALOG_SECTIONS[2:]) if index["schemaVersion"] == 3 else [])


def canonical(index, sections=None):
    validate(index)
    # Do not normalize paths/case or discard project/definition/kind: those are semantics.
    for name in ("projects", "types", "methods", "references"):
        if name not in index:
            raise ValueError(f"Missing analysis field: {name}")
    fields = semantic_sections(index) if sections is None else sections
    result = normalize({key: index.get(key) for key in fields})
    if "references" in result:
        result["references"] = {key: sorted(values, key=encoded) for key, values in result["references"].items()}
    duplicates = result.get("duplicates")
    if isinstance(duplicates, dict):
        # Generation/build provenance is retained in raw artifacts, not analysis equality.
        if isinstance(duplicates.get("scan"), dict):
            duplicates["scan"].pop("indexId", None)
        if isinstance(duplicates.get("analysis"), dict):
            duplicates["analysis"].pop("semanticToolHash", None)
    return result


def compare_analysis(baseline, candidate):
    validate(baseline)
    validate(candidate)
    old_sections, new_sections = semantic_sections(baseline), semantic_sections(candidate)
    sections = [section for section in old_sections if section in new_sections]
    scope = {"complete": old_sections == new_sections, "sections": sections,
             "excludedSections": [section for section in dict.fromkeys(old_sections + new_sections) if section not in sections],
             "baselineSchemaVersion": baseline["schemaVersion"], "candidateSchemaVersion": candidate["schemaVersion"]}
    return canonical(baseline, sections), canonical(candidate, sections), scope


def provenance(index):
    duplicates = index.get("duplicates") or {}
    analysis = duplicates.get("analysis") if isinstance(duplicates.get("analysis"), dict) else {}
    scan = duplicates.get("scan") if isinstance(duplicates.get("scan"), dict) else {}
    return {"toolVersion": index.get("toolVersion"), "duplicateSemanticToolHash": analysis.get("semanticToolHash"),
            "duplicateScanIndexId": scan.get("indexId")}


def absolute_path(value):
    return isinstance(value, str) and bool(value) and (Path(value).is_absolute() or PureWindowsPath(value).is_absolute())


def attach_catalog(index, catalog):
    if not isinstance(catalog, dict):
        raise ValueError("Catalog must be an object")
    unexpected = set(catalog) - set(CATALOG_SECTIONS) - {"indexId", "solution"}
    if unexpected:
        raise ValueError("Unsupported catalog fields: " + ", ".join(sorted(unexpected)))
    if "indexId" in catalog or "solution" in catalog:
        if (catalog.get("indexId") != index.get("indexId") or not absolute_path(catalog.get("solution"))
                or not absolute_path(index.get("solution"))
                or os.path.normcase(catalog["solution"]) != os.path.normcase(index["solution"])):
            raise ValueError("Snapshot and catalog identities disagree")
    return dict(index, **{field: catalog.get(field) for field in CATALOG_SECTIONS})


def load_index(path, allow_unverified_raw=False, integrity=None):
    """Resolve once, verify complete generation, and pair only that generation's catalog."""
    path = Path(path).resolve()
    try:
        retained = path.parent / "generation.json"
        manifest_path = path if path.name == "current.json" else (
            retained if path.name == "index.json" and retained.exists() else None)
        if manifest_path is not None:
            manifest_bytes = manifest_path.read_bytes()
            if len(manifest_bytes) > 64 * 1024:
                raise ValueError("Snapshot manifest is unexpectedly large")
            manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
            if isinstance(manifest, dict) and manifest.get("manifestVersion") == 2:
                raise ValueError("SQLite generation (manifest version 2); compare its JSON export from "
                                 "'codeatlas export-json --index <current.json> --output <new-directory>'")
            if (not isinstance(manifest, dict) or type(manifest.get("manifestVersion")) is not int
                    or manifest["manifestVersion"] != 1):
                raise ValueError("Unsupported or invalid snapshot manifest")
            identifier = manifest.get("indexId")
            if (not isinstance(identifier, str) or re.fullmatch(r"[0-9a-fA-F]{32}", identifier) is None
                    or not absolute_path(manifest.get("solution"))):
                raise ValueError("Invalid manifest identity")
            artifacts = manifest.get("artifacts")
            expected = ARTIFACTS | ({"catalog.json"} if isinstance(artifacts, dict) and "catalog.json" in artifacts else set())
            if not isinstance(artifacts, dict) or set(artifacts) != expected:
                raise ValueError("Invalid manifest artifact inventory")
            directory = manifest_path.parent if manifest_path.name == "generation.json" else manifest_path.parent / "generations" / identifier
            index = catalog = None
            for name, digest in artifacts.items():
                if (not isinstance(digest, dict) or type(digest.get("length")) is not int or digest["length"] < 0
                        or not isinstance(digest.get("sha256"), str) or re.fullmatch(r"[0-9a-fA-F]{64}", digest["sha256"]) is None):
                    raise ValueError("Invalid artifact digest: " + name)
                data = (directory / name).read_bytes()
                if len(data) != digest["length"] or hashlib.sha256(data).hexdigest().lower() != digest["sha256"].lower():
                    raise ValueError("Snapshot artifact checksum/length mismatch: " + name)
                if name == "index.json":
                    index = json.loads(data.decode("utf-8-sig"))
                elif name == "catalog.json":
                    catalog = json.loads(data.decode("utf-8-sig"))
            if (not isinstance(index, dict) or index.get("indexId") != identifier
                    or not absolute_path(index.get("solution"))
                    or os.path.normcase(index["solution"]) != os.path.normcase(manifest["solution"])):
                raise ValueError("Snapshot and manifest identities disagree")
            if "catalog.json" in artifacts:
                index = attach_catalog(index, catalog)
            if integrity is not None:
                integrity.update(status="verified-generation", manifest=str(manifest_path), indexId=identifier)
        else:
            index = json.loads(path.read_text(encoding="utf-8-sig"))
            sibling = path.parent / "catalog.json"
            if isinstance(index, dict) and index.get("schemaVersion", 0) in (2, 3) and index.get("symbols") is None and sibling.exists():
                if not allow_unverified_raw:
                    raise ValueError("Split snapshot has no retained manifest; use its current.json or --allow-unverified-raw for legacy inspection")
                index = attach_catalog(index, json.loads(sibling.read_text(encoding="utf-8-sig")))
            if integrity is not None:
                integrity.update(status="unverified-raw", path=str(path))
        validate(index)
        return index
    except OSError as error:
        raise ValueError(f"Cannot read snapshot artifact: {error}") from error


def validate(index):
    """Validate each supported schema before selecting a possibly partial common scope."""
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
    require(type(index.get("schemaVersion")) is int and index["schemaVersion"] in (1, 2, 3),
            "Unsupported or missing schemaVersion (expected 1, 2, or 3)")
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
    if index["schemaVersion"] >= 2:
        require(isinstance(index.get("symbols"), list), "symbols must be an array")
        require(isinstance(index.get("documentationCoverage"), dict), "documentationCoverage must be an object")
        require(index["documentationCoverage"].get("version") == 1, "Unsupported documentation coverage")
        symbol_ids = []
        for symbol in index["symbols"]:
            located(symbol, ("id", "name", "kind", "signature", "project"), "symbol")
            symbol_ids.append(symbol["id"])
            require(isinstance(symbol.get("documentation"), dict), "symbol.documentation must be an object")
        require(len(symbol_ids) == len(set(symbol_ids)), "Duplicate symbol IDs")
    if index["schemaVersion"] == 3:
        catalog = index.get("structuralCatalog")
        require(isinstance(catalog, dict), "structuralCatalog must be an object")
        require(isinstance(catalog.get("facts"), list) and isinstance(catalog.get("issues"), list),
                "structuralCatalog facts and issues must be arrays")
        require(isinstance(catalog.get("coverage"), dict) and catalog["coverage"].get("version") == 1,
                "Unsupported structural coverage")
        duplicates = index.get("duplicates")
        if duplicates is not None:
            require(isinstance(duplicates, dict) and type(duplicates.get("version")) is int
                    and duplicates["version"] in (1, 2), "Unsupported duplicate catalog")
            require(isinstance(duplicates.get("groups"), list), "duplicate groups must be an array")
            for group in duplicates["groups"]:
                record(group, ("hash", "reason"), "duplicate group")
                require(strings(group.get("memberIds")) and len(group["memberIds"]) >= 2,
                        "Duplicate members must be an array of at least two IDs")
            if duplicates["version"] == 2:
                require(isinstance(duplicates.get("analysis"), dict) and isinstance(duplicates.get("scan"), dict),
                        "Current duplicate catalog requires analysis and scan evidence")
                analysis, scan = duplicates["analysis"], duplicates["scan"]
                require(analysis.get("version") == "structural-v1" and scan.get("detectorVersion") == "aligned-semantic-v1",
                        "Unsupported duplicate analysis/detector version")
                require(scan.get("indexId") == index.get("indexId"), "Duplicate scan and index identities disagree")
                require(isinstance(analysis.get("semanticToolHash"), str)
                        and re.fullmatch(r"[0-9a-fA-F]{64}", analysis["semanticToolHash"]) is not None,
                        "Invalid duplicate semantic tool hash")
                require(strings(analysis.get("vocabulary")) and strings(analysis.get("budgetNotices"))
                        and strings(scan.get("limitations")), "Invalid duplicate vocabulary/notices/limitations")
                for container, fields in ((analysis, ("bodies", "omissions")), (scan, ("candidates", "omissions"))):
                    for field in fields:
                        require(isinstance(container.get(field), list) and all(isinstance(item, dict) for item in container[field]),
                                f"duplicate {field} must be an array of objects")
                for container in (analysis, scan):
                    coverage = container.get("coverage")
                    require(isinstance(coverage, dict) and coverage.get("version") == "structural-v1"
                            and strings(coverage.get("supported")) and strings(coverage.get("exclusions"))
                            and coverage.get("completeForRealizedContexts") is False,
                            "Invalid duplicate coverage")
                for limits in (analysis.get("limits"), scan.get("detectionLimits")):
                    require(isinstance(limits, dict), "Duplicate limits must be an object")
                    for field in ("maximumMethods", "maximumTokensPerBody", "maximumTotalTokens", "maximumCatalogBytes",
                                  "minimumTokens", "maximumPosting", "maximumPairVisits", "maximumComparisons",
                                  "maximumFindings", "maximumShinglesPerBody", "maximumPostingKeys", "maximumPostingEntries", "maximumScanBytes"):
                        require(type(limits.get(field)) is int and limits[field] > 0, f"Invalid duplicate limit: {field}")
                    score = limits.get("minimumNearScore")
                    require(type(score) in (int, float) and 0.5 <= score <= 1, "Invalid duplicate minimum score")
                body_ids = set()
                for body in analysis["bodies"]:
                    record(body, ("symbolId", "project", "containingType", "fileHash", "contextHash", "signatureKey", "lexicalHash", "normalizedHash"), "duplicate body")
                    require(body["symbolId"] in method_ids and body["symbolId"] not in body_ids, "Invalid duplicate body owner")
                    body_ids.add(body["symbolId"])
                    for field in ("tokens", "tokenOffsets", "tokenLengths"):
                        require(isinstance(body.get(field), list) and all(type(item) is int for item in body[field]),
                                f"Invalid duplicate body {field}")
                    require(len(body["tokens"]) == len(body["tokenOffsets"]) == len(body["tokenLengths"])
                            and all(0 <= token < len(analysis["vocabulary"]) for token in body["tokens"])
                            and all(offset >= 0 for offset in body["tokenOffsets"])
                            and all(length > 0 for length in body["tokenLengths"]), "Invalid duplicate token evidence")
                    require(isinstance(body.get("names"), list) and all(isinstance(name, dict) for name in body["names"]),
                            "Invalid duplicate name evidence")
                require(len(duplicates["groups"]) == len(scan["candidates"]), "Duplicate groups and candidates disagree")
                for candidate in scan["candidates"]:
                    record(candidate, ("pairId", "kind", "scoreDefinition"), "duplicate candidate")
                    for side in ("left", "right"):
                        record(candidate.get(side), ("symbolId", "project", "fileHash", "contextHash"), "duplicate candidate " + side)
                        require(candidate[side]["symbolId"] in body_ids, "Unknown duplicate candidate body")
                    require(type(candidate.get("score")) in (int, float) and 0 <= candidate["score"] <= 1
                            and strings(candidate.get("reasons")) and strings(candidate.get("caveats"))
                            and isinstance(candidate.get("importantDifferences"), list), "Invalid duplicate candidate evidence")
                work = scan.get("work")
                require(isinstance(work, dict), "Duplicate work must be an object")
                for field in ("eligibleMethods", "filteredTrivialMethods", "suppressedPostings", "pairVisits", "generatedPairs", "comparedPairs", "retainedFindings"):
                    require(type(work.get(field)) is int and work[field] >= 0, f"Invalid duplicate work: {field}")
                require(type(work.get("workCapped")) is bool and strings(work.get("capReasons"))
                        and work["workCapped"] == bool(work["capReasons"])
                        and work["retainedFindings"] == len(scan["candidates"]), "Inconsistent duplicate work evidence")
    if "fingerprints" in index:
        require(isinstance(index["fingerprints"], dict) and
                all(isinstance(path, str) and isinstance(digest, str) for path, digest in index["fingerprints"].items()),
                "fingerprints must be a string-to-string object")


def differences(before, after):
    result = {}
    for section in (*BASE_SECTIONS, *CATALOG_SECTIONS):
        if section not in before and section not in after:
            continue
        if section in ("documentationCoverage", "structuralCatalog", "duplicates"):
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
    parser.add_argument("--allow-unverified-raw", action="store_true",
                        help="Explicit legacy inspection: permit pairing a raw split index with its unverified sibling catalog")
    args = parser.parse_args()
    try:
        baseline_integrity, candidate_integrity = {}, {}
        baseline = load_index(args.baseline, args.allow_unverified_raw, baseline_integrity)
        candidate = load_index(args.candidate, args.allow_unverified_raw, candidate_integrity)
        before, after, scope = compare_analysis(baseline, candidate)
        delta = differences(before, after)
        report = {
            "equivalent": before == after,
            "comparisonScope": scope,
            "baselineIntegrity": baseline_integrity,
            "candidateIntegrity": candidate_integrity,
            "baselineProvenance": provenance(baseline),
            "candidateProvenance": provenance(candidate),
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
