"""Run one index benchmark, retaining logs, timings and main-process memory evidence.

Usage: python scripts/benchmark-index.py --label baseline-1
       --cli C:/Code/CodeAtlas/CodeAtlas.Net/src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll
       --solution C:/LocalCode/eConnectorV2Git/econnectorv2.sln --retain-index

No source changes; generated target artifacts are produced by the normal CLI.
Main-process working set excludes MSBuild child processes and is not total-tree RAM.
A final counter read is attempted after exit; if unavailable, the last sampling
interval can contain an unobserved peak. The report records that limitation.
"""
import argparse
import ctypes
from ctypes import wintypes
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import time


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage")]


def retain_index(result, output):
    """Retain one complete immutable generation, never a split index without its catalog."""
    snapshot = Path(result.get("snapshotIndex", result["index"]))
    mirror = snapshot.parent / "generation.json"
    manifest_path = mirror if mirror.exists() else Path(result["index"])
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    if not isinstance(manifest, dict) or "manifestVersion" not in manifest:
        # Pre-generation CLI versions produced complete inline indexes.
        index = json.loads(snapshot.read_text(encoding="utf-8-sig"))
        if (not isinstance(index, dict) or index.get("schemaVersion") not in (1, 2, 3)
                or index.get("schemaVersion") >= 2 and index.get("symbols") is None):
            raise ValueError("Cannot retain an incomplete raw split index without its generation manifest")
        destination = output / "index.json"
        shutil.copyfile(snapshot, destination)
        return {"index": str(destination), "snapshotIndex": str(destination)}
    identifier = manifest.get("indexId")
    artifacts = manifest.get("artifacts")
    expected = {"index.json", "overview.md", "warnings.txt", "timings.json"}
    if isinstance(artifacts, dict) and "catalog.json" in artifacts:
        expected.add("catalog.json")
    if (manifest.get("manifestVersion") != 1 or not isinstance(identifier, str)
            or re.fullmatch(r"[0-9a-fA-F]{32}", identifier) is None
            or result.get("indexId", identifier) != identifier
            or not isinstance(artifacts, dict) or set(artifacts) != expected):
        raise ValueError("Benchmark result and retained generation manifest disagree")
    source = snapshot.parent if snapshot.name == "index.json" else manifest_path.parent / "generations" / identifier
    destination = output / "generations" / identifier
    destination.mkdir(parents=True, exist_ok=False)
    for name in sorted(expected):
        digest = artifacts[name]
        target = destination / name
        shutil.copyfile(source / name, target)
        with target.open("rb") as stream:
            checksum = hashlib.file_digest(stream, "sha256").hexdigest()
        if (not isinstance(digest, dict) or target.stat().st_size != digest.get("length")
                or checksum.lower() != str(digest.get("sha256")).lower()):
            raise ValueError("Retained snapshot artifact checksum/length mismatch: " + name)
    # All copies use the captured immutable manifest, even if the active pointer moves.
    for target in (destination / "generation.json", output / "current.json", output / "manifest.json"):
        target.write_bytes(manifest_bytes)
    return {"index": str(output / "current.json"), "snapshotIndex": str(destination / "index.json")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--solution", type=Path, required=True)
    parser.add_argument("--outdir", type=Path, default=Path(__file__).resolve().parents[1] / ".codeatlas" / "benchmarks")
    parser.add_argument("--retain-index", action="store_true")
    parser.add_argument("--reference-workers", type=int, choices=range(1, 9),
                        help="Override candidate worker count; omit when running older CLIs")
    parser.add_argument("--conditions", default="Other machine load uncontrolled",
                        help="Record known competing activity or idle-machine conditions")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This benchmark samples Windows main-process memory.")
    if not args.label or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for c in args.label):
        parser.error("Label must contain letters, digits, hyphens or underscores only.")
    cli, solution = args.cli.resolve(strict=True), args.solution.resolve(strict=True)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output = args.outdir.resolve() / (stamp + "-" + args.label)
    output.mkdir(parents=True, exist_ok=False)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        parser.error("dotnet was not found on PATH")
    sdk = subprocess.run([dotnet, "--info"], capture_output=True, text=True, check=True).stdout
    (output / "dotnet-info.txt").write_text(sdk, encoding="utf-8")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    command = [dotnet, str(cli), "index", "--solution", str(solution)]
    if args.reference_workers is not None:
        command.extend(["--reference-workers", str(args.reference_workers)])
    metadata = {
        "label": args.label, "startedAtUtc": stamp, "command": command,
        "cwd": str(Path.cwd()), "platform": platform.platform(),
        "machine": platform.machine(), "processor": platform.processor(),
        "logicalProcessors": os.cpu_count(), "sampleIntervalSeconds": 0.5,
        "memoryScope": "main CLI process only; MSBuild child processes excluded",
        "conditions": "fresh CLI process; filesystem and OS caches uncontrolled; " + args.conditions,
        "referenceWorkers": args.reference_workers,
        "binarySha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (cli, cli.parent / "CodeAtlas.Core.dll") if path.is_file()},
    }
    samples = []
    start = time.perf_counter()
    with (output / "stdout.json").open("wb") as stdout, (output / "stderr.log").open("wb") as stderr:
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr)
        handle = kernel.OpenProcess(0x0400 | 0x0010, False, process.pid)
        if not handle:
            metadata["memorySamplingError"] = str(ctypes.WinError(ctypes.get_last_error()))

        def sample_memory(after_exit=False):
            counters = ProcessMemoryCounters()
            counters.cb = ctypes.sizeof(counters)
            if handle and psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                samples.append({"elapsedSeconds": round(time.perf_counter() - start, 3),
                                "workingSetBytes": counters.WorkingSetSize,
                                "peakWorkingSetBytes": counters.PeakWorkingSetSize,
                                "pagefileBytes": counters.PagefileUsage,
                                "afterExit": after_exit})
                return True
            if after_exit and handle:
                metadata["finalMemorySamplingError"] = str(ctypes.WinError(ctypes.get_last_error()))
            return False

        try:
            while process.poll() is None:
                sample_memory()
                time.sleep(0.5)
            metadata["finalMemorySampleSucceeded"] = sample_memory(after_exit=True)
            metadata["memoryPeakCoverage"] = (
                "Includes an OS peak-counter read after process exit"
                if metadata["finalMemorySampleSucceeded"] else
                "Sampled OS peak counters only; the final sampling interval may contain an unobserved peak")
        finally:
            if handle:
                kernel.CloseHandle(handle)
        metadata.update(exitCode=process.returncode, wallSeconds=round(time.perf_counter() - start, 3),
                        peakWorkingSetBytes=max((s["peakWorkingSetBytes"] for s in samples), default=None),
                        sampleCount=len(samples))
    if process.returncode == 0:
        result = json.loads((output / "stdout.json").read_text(encoding="utf-8-sig"))
        metadata["result"] = result
        if result.get("timings"):
            shutil.copyfile(result["timings"], output / "timings.json")
        if args.retain_index:
            metadata["retainedSnapshot"] = retain_index(result, output)
    (output / "memory-samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (output / "benchmark.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "benchmark.json"), "exitCode": process.returncode,
                      "wallSeconds": metadata["wallSeconds"],
                      "peakWorkingSetBytes": metadata["peakWorkingSetBytes"]}, indent=2))
    return process.returncode


if __name__ == "__main__":
    raise SystemExit(main())
