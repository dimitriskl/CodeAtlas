"""Measure fresh-process index queries without changing source or weakening freshness.

Run --mode baseline --profile against the instrumented full-index implementation
before making a storage decision. Candidate mode adds filter/page cases. Inputs
and complete outputs remain in ignored .codeatlas/query-benchmarks. Windows only.
"""
import argparse
import collections
import ctypes
from ctypes import wintypes
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import time

BASELINE_CASES = ("name-hit", "name-miss", "busiest-references")


def validate_baseline(baseline, scope, snapshot_id):
    if not isinstance(baseline, dict) or baseline.get("mode") != "baseline":
        raise ValueError("Candidate requires a baseline-mode report")
    if baseline.get("scope") != scope or baseline.get("snapshotId") != snapshot_id:
        raise ValueError("Baseline scope and snapshot must match the candidate")
    if baseline.get("passed") is not True:
        raise ValueError("Baseline report must have passed its declared budgets")
    cases = baseline.get("cases")
    if not isinstance(cases, dict):
        raise ValueError("Baseline cases are missing")
    for name in BASELINE_CASES:
        case = cases.get(name)
        assessment = case.get("assessment") if isinstance(case, dict) else None
        if not isinstance(assessment, dict) or assessment.get("passed") is not True:
            raise ValueError(f"Missing passing baseline assessment: {name}")
        for field in ("medianWallSeconds", "peakWorkingSetBytes"):
            number = assessment.get(field)
            if type(number) not in (int, float) or not math.isfinite(number) or number <= 0:
                raise ValueError(f"Invalid baseline assessment: {name}.{field}")


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage")]


def reference_key(reference):
    location = reference["location"]
    return (location["file"], location["line"], location["column"],
            reference["definitionId"], reference["project"], reference["kind"])


def cases_for(index, candidate):
    counts = collections.Counter(method["name"] for method in index["methods"])
    name = min(counts, key=lambda value: (-counts[value], value))
    methods = sorted((method for method in index["methods"] if method["name"] == name), key=lambda m: m["id"])
    symbol = min(index["references"], key=lambda value: (-len(index["references"][value]), value))
    references = sorted(index["references"][symbol], key=reference_key)
    missing = "__CodeAtlas_Performance_Missing_Method__"
    while missing in counts:
        missing += "_"
    limit = 100 if candidate else None
    cases = [
        ("name-hit", ["find-symbol", "--name", name], "symbols", methods[:limit], len(methods)),
        ("name-miss", ["find-symbol", "--name", missing], "symbols", [], 0),
        ("busiest-references", ["find-references", "--symbol", symbol], "references", references[:limit], len(references)),
    ]
    if candidate:
        project = methods[0]["project"]
        filtered = [method for method in methods if method["project"] == project]
        cases.extend([
            ("project-filter", ["find-symbol", "--name", name, "--project", project], "symbols", filtered[:100], len(filtered)),
            ("references-page1", ["find-references", "--symbol", symbol, "--limit", "1"], "references", references[:1], len(references)),
            ("references-page2", ["find-references", "--symbol", symbol, "--limit", "1", "--offset", "1", "--snapshot", index["indexId"]], "references", references[1:2], len(references)),
        ])
    return cases


def check_result(result, field, expected, total, snapshot_id, candidate):
    if result.get("indexId") != snapshot_id:
        raise ValueError("Snapshot changed during measurement")
    def normalize(item):
        # Legacy schema-1 methods omit this additive nullable field. Every other
        # field, including signatures and complete source positions, must match.
        normalized = dict(item)
        if field == "symbols":
            normalized.setdefault("containingType", None)
        return normalized
    if [normalize(item) for item in result[field]] != [normalize(item) for item in expected]:
        raise ValueError("Query result differs from complete retained index expectations")
    if candidate and result["pagination"]["total"] != total:
        raise ValueError("Reported total differs from complete retained index expectations")


def assess(samples, scope, baseline=None):
    median_limit, maximum_limit, memory_limit = (1, 2, 192 * 1024**2) if scope == "small" else (3, 5, 768 * 1024**2)
    median = statistics.median(sample["wallSeconds"] for sample in samples)
    maximum = max(sample["wallSeconds"] for sample in samples)
    memory = max(sample["peakWorkingSetBytes"] for sample in samples)
    failures = []
    if median > median_limit:
        failures.append("median_wall")
    if maximum > maximum_limit:
        failures.append("maximum_wall")
    if memory > memory_limit:
        failures.append("peak_working_set")
    if baseline:
        if median > baseline["medianWallSeconds"] * 1.25 + 0.1:
            failures.append("relative_median_wall")
        if memory > baseline["peakWorkingSetBytes"] * 1.25 + 32 * 1024**2:
            failures.append("relative_peak_working_set")
    return {"medianWallSeconds": median, "maximumWallSeconds": maximum,
            "peakWorkingSetBytes": memory, "budgetFailures": failures, "passed": not failures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--index", type=Path, required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--scope", choices=["small", "large"], required=True)
    parser.add_argument("--mode", choices=["baseline", "candidate"], required=True)
    parser.add_argument("--profile", action="store_true")
    parser.add_argument("--baseline", type=Path, help="Required baseline-mode report.json for candidate relative budgets")
    parser.add_argument("--outdir", type=Path, default=Path(__file__).resolve().parents[1] / ".codeatlas" / "query-benchmarks")
    parser.add_argument("--conditions", default="Other machine load uncontrolled")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Windows is required for OS peak working-set measurement")
    if not args.label or any(char not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for char in args.label):
        parser.error("Label must contain only letters, digits, hyphens and underscores")
    cli, index_path = args.cli.resolve(strict=True), args.index.resolve(strict=True)
    index = json.loads(index_path.read_text(encoding="utf-8-sig"))
    candidate = args.mode == "candidate"
    if candidate != (args.baseline is not None):
        parser.error("--baseline is required for candidate mode and must be omitted for baseline mode")
    baseline = json.loads(args.baseline.read_text(encoding="utf-8-sig")) if args.baseline else None
    if candidate:
        try:
            validate_baseline(baseline, args.scope, index["indexId"])
        except ValueError as error:
            parser.error(str(error))
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        parser.error("dotnet not found")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output = args.outdir.resolve() / (stamp + "-" + args.label)
    output.mkdir(parents=True, exist_ok=False)
    (output / "dotnet-info.txt").write_text(subprocess.run([dotnet, "--info"], capture_output=True, text=True, check=True).stdout, encoding="utf-8")
    report = {
        "label": args.label, "mode": args.mode, "scope": args.scope, "snapshotId": index["indexId"],
        "indexBytes": index_path.stat().st_size, "indexPath": str(index_path), "startedAtUtc": stamp,
        "platform": platform.platform(), "processor": platform.processor(), "logicalProcessors": os.cpu_count(),
        "conditions": "Five fresh processes after one untimed warmup per case; OS/filesystem caches uncontrolled; " + args.conditions,
        "memoryScope": "CLI main process OS peak working set; no MSBuild evaluation in indexed queries",
        "sampleIntervalSeconds": 0.01,
        "binarySha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (cli, cli.parent / "CodeAtlas.Core.dll")}, "cases": {}}
    if baseline is not None:
        report["baseline"] = {
            "path": str(args.baseline.resolve()),
            "sha256": hashlib.sha256(args.baseline.read_bytes()).hexdigest(),
            "label": baseline.get("label"), "startedAtUtc": baseline.get("startedAtUtc"),
            "mode": baseline["mode"], "scope": baseline["scope"], "snapshotId": baseline["snapshotId"]}
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL

    def run(command, directory):
        directory.mkdir()
        peak, observations = 0, 0
        started = time.perf_counter()
        with (directory / "stdout.json").open("wb") as stdout, (directory / "stderr.log").open("wb") as stderr:
            process = subprocess.Popen(command, stdout=stdout, stderr=stderr)
            handle = kernel.OpenProcess(0x0400 | 0x0010, False, process.pid)
            if not handle:
                process.wait()
                raise ctypes.WinError(ctypes.get_last_error())
            def sample():
                nonlocal peak, observations
                counters = ProcessMemoryCounters()
                counters.cb = ctypes.sizeof(counters)
                if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                    peak = max(peak, counters.PeakWorkingSetSize)
                    observations += 1
                    return True
                return False
            try:
                while process.poll() is None:
                    sample()
                    time.sleep(0.01)
                final_sample = sample()
                wall = time.perf_counter() - started
            finally:
                kernel.CloseHandle(handle)
        if process.returncode != 0:
            raise RuntimeError(f"Query failed with exit {process.returncode}; see {directory}")
        if observations == 0:
            raise RuntimeError("No memory samples captured")
        result = json.loads((directory / "stdout.json").read_text(encoding="utf-8-sig"))
        return result, {"wallSeconds": wall, "peakWorkingSetBytes": peak, "memorySamples": observations,
                        "finalMemorySampleSucceeded": final_sample,
                        "memoryPeakCoverage": "post-exit OS peak" if final_sample else "sampled OS peaks; final interval may be unobserved",
                        "queryTimings": result.get("queryTimings")}

    try:
        for label, command_args, field, expected, total in cases_for(index, candidate):
            command = [dotnet, str(cli), *command_args, "--index", str(index_path)]
            if args.profile:
                command.append("--profile")
            case_dir = output / label
            case_dir.mkdir()
            result, warmup = run(command, case_dir / "warmup")
            check_result(result, field, expected, total, index["indexId"], candidate)
            samples = []
            for number in range(1, 6):
                result, sample = run(command, case_dir / str(number))
                check_result(result, field, expected, total, index["indexId"], candidate)
                samples.append(sample)
            baseline_case = baseline["cases"][label]["assessment"] if baseline and label in BASELINE_CASES else None
            report["cases"][label] = {"command": command, "warmupExcluded": warmup,
                                      "samples": samples, "baselineAssessment": baseline_case,
                                      "assessment": assess(samples, args.scope, baseline_case)}
        report["passed"] = all(case["assessment"]["passed"] for case in report["cases"].values())
    except Exception as error:
        report["passed"] = False
        report["failure"] = str(error)
    finally:
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "report.json"), "passed": report["passed"]}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
