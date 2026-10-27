"""Run the benchmark's CodeAtlas CLI copy with a fixed, recorded environment.

CodeAtlas freshness digests the whole process environment. An agent's shell differs
from the shell that built the index (PWD, agent variables), which would make every
query refuse with stale_index. prepare copies this file next to codeatlas.json, which
names the CLI, the working directory and a whitelisted environment without secrets.
Arguments are passed through unchanged; stdout, stderr and the exit code are preserved.
"""
import json
from pathlib import Path
import subprocess
import sys


def main():
    config = json.loads((Path(__file__).resolve().parent / "codeatlas.json").read_text(encoding="utf-8"))
    completed = subprocess.run([config["cli"], *sys.argv[1:]], cwd=config["cwd"], env=config["environment"])
    return completed.returncode


if __name__ == "__main__":
    sys.exit(main())
