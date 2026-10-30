#!/usr/bin/env python3
"""Time codeatlas live on one already-indexed solution.

Cold load is the time until the host is reading a request. Each operation then
runs one uncounted warmup and the requested measured rounds. Medians and p95
use nearest rank on those rounds. An edit is applied in memory and published
by the host; the original text is published again before exit so a later
freshness check still matches the files on disk.
"""

import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time


def percentile(samples, percent):
    ordered = sorted(samples)
    rank = max(1, math.ceil(percent / 100 * len(ordered)))
    return ordered[rank - 1]


def summarize(samples):
    return {
        "rounds": len(samples),
        "medianMs": round(statistics.median(samples), 1),
        "p95Ms": round(percentile(samples, 95), 1),
        "minMs": round(min(samples), 1),
        "maxMs": round(max(samples), 1),
    }


def wait_until_reading(process, timeout):
    started = time.perf_counter()
    stdin_inode = os.readlink(f"/proc/{process.pid}/fd/0")
    while time.perf_counter() - started < timeout:
        if process.poll() is not None:
            raise RuntimeError(process.stderr.read()[-1200:])
        for task in os.listdir(f"/proc/{process.pid}/task"):
            try:
                parts = open(f"/proc/{process.pid}/task/{task}/syscall").read().split()
            except FileNotFoundError:
                continue
            if not parts or parts[0] != "0":
                continue
            fd = int(parts[1], 16)
            try:
                target = os.readlink(f"/proc/{process.pid}/fd/{fd}")
            except OSError:
                continue
            if target == stdin_inode:
                return (time.perf_counter() - started) * 1000
        time.sleep(0.05)
    raise TimeoutError("the live host did not start reading requests")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, help="path to codeatlas.dll")
    parser.add_argument("--solution", required=True)
    parser.add_argument("--rounds", type=int, default=20)
    parser.add_argument("--symbol", default="HandleRequest")
    parser.add_argument("--query", default="authenticate user password")
    parser.add_argument("--references", required=True, help="symbol id for find-references")
    parser.add_argument("--edit", required=True, help="source file edited and then restored through the host")
    parser.add_argument("--startup-timeout", type=float, default=180)
    args = parser.parse_args()
    if args.rounds < 2:
        parser.error("--rounds must be at least 2 so one warmup can be excluded")

    with open(args.edit, encoding="utf-8") as source:
        original = source.read()
    process = subprocess.Popen(
        ["dotnet", args.cli, "live", "--solution", args.solution],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        cwd=os.path.dirname(os.path.abspath(args.solution)))
    edited = False
    try:
        cold_ms = wait_until_reading(process, args.startup_timeout)
        print(f"cold-load {cold_ms:.1f}", flush=True)

        def call(payload):
            started = time.perf_counter()
            process.stdin.write(json.dumps(payload) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            elapsed = (time.perf_counter() - started) * 1000
            if not line:
                raise RuntimeError(process.stderr.read()[-1200:])
            body = json.loads(line)
            if not body.get("success"):
                raise RuntimeError(json.dumps(body.get("error")))
            return elapsed, body

        def measure(payload):
            warmup, _ = call(payload)
            samples = [call(payload)[0] for _ in range(args.rounds)]
            print(f"{payload['command']} warmup {warmup:.1f} {summarize(samples)}", flush=True)
            return {"warmupMs": round(warmup, 1), **summarize(samples)}

        loaded = {
            "find-symbol": measure({"command": "find-symbol", "name": args.symbol}),
            "search": measure({"command": "search", "query": args.query}),
            "find-references": measure({"command": "find-references", "symbol": args.references}),
        }
        edit_ms, _ = call({"command": "edit", "path": args.edit, "text": original + "\n// live-host measurement\n"})
        edited = True
        print(f"edit {edit_ms:.1f}", flush=True)
        after_edit = {
            "find-symbol": measure({"command": "find-symbol", "name": args.symbol}),
            "search": measure({"command": "search", "query": args.query}),
            "find-references": measure({"command": "find-references", "symbol": args.references}),
        }
        call({"command": "edit", "path": args.edit, "text": original})
        edited = False
    finally:
        if edited and process.poll() is None:
            try:
                process.stdin.write(json.dumps({"command": "edit", "path": args.edit, "text": original}) + "\n")
                process.stdin.flush()
                process.stdout.readline()
            except OSError:
                pass
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)

    report = {
        "coldLoadMs": round(cold_ms, 1),
        "editMs": round(edit_ms, 1),
        "loaded": loaded,
        "afterEdit": after_edit,
        "targetMs": 300,
        "symbol": args.symbol,
        "query": args.query,
        "references": args.references,
        "edit": args.edit,
        "rounds": args.rounds,
    }
    json.dump(report, sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
