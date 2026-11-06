"""Future report formatter only. It never builds, runs a detector, loads a solution or reads private data."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--acceptance", type=Path, required=True)
    parser.add_argument("--new-output", type=Path, required=True)
    parser.add_argument("--public-directory", type=Path, required=True)
    args = parser.parse_args()
    public = args.public_directory.resolve()
    freeze = json.loads((public / "FREEZE.json").read_text(encoding="utf-8"))
    for name in ("Corpus.cs", "labels.json", "PROTOCOL.md"):
        actual = hashlib.sha256((public / name).read_bytes()).hexdigest().upper()
        if actual != freeze["files"][name]:
            raise ValueError(f"Frozen input changed: {name}")
    acceptance_bytes = args.acceptance.read_bytes()
    acceptance = json.loads(acceptance_bytes)
    if acceptance.get("accepted") is not True or not acceptance.get("acceptedBy") or not acceptance.get("acceptedAtUtc"):
        raise ValueError("Missing explicit root/user acceptance before qualification")
    result = json.loads(args.result.read_text(encoding="utf-8"))
    if result["acceptanceSha256"] != hashlib.sha256(acceptance_bytes).hexdigest().upper():
        raise ValueError("Result and accepted configuration are not bound together")
    if result["protocol"] != freeze["protocol"] or result["detectorVersion"] != acceptance["detectorVersion"]:
        raise ValueError("Protocol/detector version mismatch")
    for field in ("detectorRevision", "executableSha256", "harnessSha256"):
        if not acceptance.get(field) or result.get(field) != acceptance[field]:
            raise ValueError(f"Frozen executable/revision mismatch: {field}")
    catalog, scan = result["duplicateCatalog"], result["duplicateScan"]
    if scan["detectionLimits"] != acceptance["limits"] or catalog["limits"] != acceptance["limits"]:
        raise ValueError("Actual limits differ from preaccepted limits")
    labels = json.loads((public / "labels.json").read_text(encoding="utf-8"))
    names = sorted({p[side] for p in labels["pairs"] for side in ("left", "right")})
    expected = {tuple(sorted((a, b))) for a, b in itertools.combinations(names, 2)}
    actual = {tuple(sorted((p["left"], p["right"]))) for p in labels["pairs"]}
    if expected != actual or len(labels["pairs"]) != labels['pairCount'] or len(names) != labels['methodCount']:
        raise ValueError("Expected exactly the frozen complete all-pair labeling")

    def name(symbol_id):
        matches = [n for n in names if f".Corpus.{n}(" in symbol_id]
        if len(matches) != 1:
            raise ValueError(f"Unknown/ambiguous public symbol: {symbol_id}")
        return matches[0]

    bodies = {name(b["symbolId"]): b for b in catalog["bodies"]}
    omissions = {}
    for omission in catalog["omissions"]:
        omissions.setdefault(name(omission["symbolId"]), []).append(omission["reason"])
    emitted = {tuple(sorted((name(c["left"]["symbolId"]), name(c["right"]["symbolId"])))): c for c in scan["candidates"]}
    traces = {}
    for event in result["trace"]:
        traces.setdefault(tuple(sorted((name(event["leftId"]), name(event["rightId"])))), []).append(event)
    rows = []
    for label in labels["pairs"]:
        a, b = label["left"], label["right"]
        key = tuple(sorted((a, b)))
        events = traces.get(key, [])
        if omissions.get(a) or omissions.get(b):
            status = "unsupported-or-budget-omitted"
        elif a not in bodies or b not in bodies:
            status = "outside-extraction-scope"
        elif min(len(bodies[a]["tokens"]), len(bodies[b]["tokens"])) < scan["detectionLimits"]["minimumTokens"]:
            status = "filtered-short-body"
        elif any(e["stage"] == "compared" for e in events):
            status = "compared"
        elif events:
            status = "generated-but-not-compared"
        else:
            status = "not-generated-by-bounded-seeds-or-caps"
        rows.append(dict(label, status=status, events=events, emitted=key in emitted,
                         candidate=emitted.get(key), omissions={a: omissions.get(a, []), b: omissions.get(b, [])}))

    def metrics(selected_rows, truth, prediction):
        tp = [r for r in selected_rows if truth(r) and prediction(r)]
        fp = [r for r in selected_rows if not truth(r) and prediction(r)]
        fn = [r for r in selected_rows if truth(r) and not prediction(r)]
        pair = lambda r: [r["left"], r["right"]]
        return dict(tp=len(tp), fp=len(fp), fn=len(fn),
                    precision=len(tp)/(len(tp)+len(fp)) if tp or fp else None,
                    recall=len(tp)/(len(tp)+len(fn)) if tp or fn else None,
                    falsePositives=[pair(r) for r in fp], falseNegatives=[pair(r) for r in fn])

    structural = metrics(rows, lambda r: r["structural"] in ("exact", "renamed-near"),
                         lambda r: r["emitted"] and r["candidate"]["kind"] in ("exact-structure", "renamed-structure"))
    useful = metrics([r for r in rows if r["usefulness"] != "domain-review"],
                     lambda r: r["usefulness"] == "review", lambda r: r["emitted"])
    rules = [r for r in rows if r['structural']=='near-with-rule-differences']
    forbidden = [r for r in rules if r['emitted'] and r['candidate']['kind'] in ('exact-structure','renamed-structure')]
    missing_rule_evidence = [r for r in rules if r['emitted'] and (not any(d['kind']=='semantic-token-or-rule' for d in r['candidate']['importantDifferences']) or not any('manual' in c.lower() for c in r['candidate']['caveats']))]
    thresholds = acceptance['metricThresholds']
    gate = all(structural[k] == thresholds['structuralExactRenamed'+k.title()] for k in ('precision','recall')) and all(useful[k] == thresholds['usefulnessExcludingDomainReview'+k.title()] for k in ('precision','recall')) and not forbidden and not missing_rule_evidence
    report = dict(protocol=freeze["protocol"], status="author-labeled-public-development-only; independent review pending", acceptedMetricGatePassed=gate,
                  fixtureRunId=result["fixtureRunId"], detectorVersion=result["detectorVersion"],
                  structuralExactRenamed=structural, usefulnessExcludingDomainReview=useful,
                  domainReview=[r for r in rows if r["usefulness"] == "domain-review"], work=scan["work"],
                  semanticDifferenceFalseExactRenamed=[[r['left'],r['right']] for r in forbidden],
                  missingEmittedRuleEvidence=[[r['left'],r['right']] for r in missing_rule_evidence],
                  limitations=scan["limitations"], pairCount=len(rows), pairs=rows)
    with args.new_output.open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
        output.write("\n")


if __name__ == "__main__":
    main()
