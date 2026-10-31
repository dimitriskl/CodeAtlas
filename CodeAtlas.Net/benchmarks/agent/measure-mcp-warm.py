#!/usr/bin/env python3
"""Time find_symbol in one codeatlas mcp process on an already-indexed solution.

The first call of a server does the one full freshness check and is reported
apart. One more call is an uncounted warmup, and the requested measured rounds
follow. For comparison, the same query then runs as a fresh find-symbol process,
with one uncounted warmup. Medians are of the measured rounds; p95 is nearest
rank. Nothing is written to the solution or its index.
"""

import argparse
import json
import math
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


class Server:
    def __init__(self, cli, solution):
        self.process = subprocess.Popen(
            ["dotnet", cli, "mcp", "--solution", solution],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", bufsize=1)
        self.next_id = 0

    def call(self, method, parameters):
        self.next_id += 1
        request = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": parameters}
        started = time.perf_counter()
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        elapsed = (time.perf_counter() - started) * 1000
        return json.loads(line), elapsed

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=30)


def find_symbol(server, workspace, name):
    arguments = {"workspace": workspace, "name": name, "kind": "method"}
    response, elapsed = server.call("tools/call", {"name": "find_symbol", "arguments": arguments})
    if response["result"]["isError"]:
        raise RuntimeError(response["result"]["content"][0]["text"][:1200])
    envelope = json.loads(response["result"]["content"][0]["text"])
    return envelope["pagination"]["total"], envelope["freshness"]["state"], elapsed


def fresh_process(cli, manifest, name):
    started = time.perf_counter()
    subprocess.run(["dotnet", cli, "find-symbol", "--index", manifest, "--name", name, "--kind", "method"],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return (time.perf_counter() - started) * 1000


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, help="path to codeatlas.dll")
    parser.add_argument("--solution", required=True)
    parser.add_argument("--workspace", required=True, help="the solution directory")
    parser.add_argument("--manifest", required=True, help="the solution's current.json")
    parser.add_argument("--name", default="HandleRequest")
    parser.add_argument("--rounds", type=int, default=20)
    parser.add_argument("--fresh-rounds", type=int, default=5)
    options = parser.parse_args()

    server = Server(options.cli, options.solution)
    server.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                               "clientInfo": {"name": "measure-mcp-warm", "version": "0"}})
    total, state, first = find_symbol(server, options.workspace, options.name)
    find_symbol(server, options.workspace, options.name)
    warm = [find_symbol(server, options.workspace, options.name)[2] for _ in range(options.rounds)]
    server.close()

    fresh_process(options.cli, options.manifest, options.name)
    fresh = [fresh_process(options.cli, options.manifest, options.name) for _ in range(options.fresh_rounds)]

    json.dump({"name": options.name, "total": total, "freshness": state, "firstCallMs": round(first, 1),
               "warm": summarize(warm), "freshProcess": summarize(fresh)}, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    main()
