#!/usr/bin/env python3
"""Run one fresh-process incremental qualification step with retained OS memory evidence.

The separate runner always uses two-pass discovery/load. Its seed/full/incremental
outputs go to an isolated same-volume generation sink, never the target pointer.
Run seed once for the fixed binaries, then at least three sequential paired
full/incremental runs with the identical --edit JSON per pair. Keep every result.
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
import shutil
import subprocess
import time


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--solution", type=Path, required=True)
    parser.add_argument("--mode", choices=("seed", "full", "incremental"), required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--edit", type=Path)
    parser.add_argument("--label", required=True)
    parser.add_argument("--conditions", required=True)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This qualification samples Windows main-process OS peak counters.")
    if not args.label or any(character not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for character in args.label):
        parser.error("Use letters, numbers, underscores and hyphens in --label.")
    if args.mode != "seed" and (args.baseline is None or args.edit is None):
        parser.error("Full/incremental require the same fixed-binary --baseline and --edit.")
    runner = args.runner.resolve(strict=True)
    root = Path(__file__).resolve().parents[1]
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output = root / ".codeatlas" / "incremental-benchmarks" / (stamp + "-" + args.label)
    output.mkdir(parents=True, exist_ok=False)
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        parser.error("dotnet is unavailable")
    command = [dotnet, str(runner), "--solution", str(args.solution.resolve(strict=True)),
               "--mode", args.mode, "--output", str(output)]
    if args.baseline is not None:
        command.extend(("--baseline", str(args.baseline.resolve(strict=True))))
    if args.edit is not None:
        edit = args.edit.resolve(strict=True)
        command.extend(("--edit", str(edit)))
        shutil.copyfile(edit, output / "edit.json")
    environment = dict(os.environ)
    removed_host_context = ["CODEX_TOOL_CALL_ID", "CODEX_THREAD_ID"]
    for name in removed_host_context:
        environment.pop(name, None)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    report = {
        "startedAtUtc": stamp, "command": command, "mode": args.mode,
        "conditions": args.conditions + "; fresh process; filesystem/OS caches uncontrolled",
        "platform": platform.platform(), "processor": platform.processor(),
        "logicalProcessors": os.cpu_count(), "sampleIntervalSeconds": 0.5,
        "memoryScope": "Main runner process only; MSBuild child processes excluded",
        "environmentProfile": {
            "removedHostContextVariables": removed_host_context,
            "scope": "Explicit premeasurement qualification context, applied before every child load and capture. All other environment values remain unchanged; the product fingerprints the complete actual child environment.",
        },
        "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "binarySha256": {file.name: hashlib.sha256(file.read_bytes()).hexdigest()
                         for file in (runner, runner.parent / "CodeAtlas.Core.dll")},
    }
    samples = []
    start = time.perf_counter()
    with (output / "stdout.json").open("wb") as stdout, (output / "stderr.log").open("wb") as stderr:
        process = subprocess.Popen(command, cwd=root, env=environment, stdout=stdout, stderr=stderr)
        handle = kernel.OpenProcess(0x0400 | 0x0010, False, process.pid)

        def sample(after_exit=False):
            counters = ProcessMemoryCounters()
            counters.cb = ctypes.sizeof(counters)
            if handle and psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                samples.append({"elapsedSeconds": round(time.perf_counter() - start, 3),
                                "workingSetBytes": counters.WorkingSetSize,
                                "peakWorkingSetBytes": counters.PeakWorkingSetSize,
                                "afterExit": after_exit})
                return True
            return False

        try:
            while process.poll() is None:
                sample()
                time.sleep(0.5)
            report["finalMemorySampleSucceeded"] = sample(after_exit=True)
        finally:
            if handle:
                kernel.CloseHandle(handle)
        report.update(exitCode=process.returncode, wallSeconds=round(time.perf_counter() - start, 3),
                      peakWorkingSetBytes=max((sample["peakWorkingSetBytes"] for sample in samples), default=None),
                      memoryPeakCoverage=("Final OS peak-counter read included" if report["finalMemorySampleSucceeded"]
                                          else "Last sampling interval can contain an unobserved peak"))
    result = output / "result.json"
    if result.exists():
        report["result"] = json.loads(result.read_text(encoding="utf-8-sig"))
    (output / "memory-samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (output / "benchmark.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(output), "exitCode": process.returncode,
                      "wallSeconds": report["wallSeconds"], "peakWorkingSetBytes": report["peakWorkingSetBytes"]}, indent=2))
    return process.returncode


if __name__ == "__main__":
    raise SystemExit(main())
