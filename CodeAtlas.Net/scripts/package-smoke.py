"""Standalone local-feed installation and freshness smoke.

Requires only a tool nupkg, the prepared public fixture zip, an installed pinned
SDK, Python and a NEW scratch directory. It never builds CodeAtlas from source.
Do not run with python -O: assertions are qualification gates. Use a new
scratch directory; this script never removes an existing directory.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--upgrade-from", type=Path,
                        help="Optional older local tool package to install before updating")
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    if not __debug__:
        raise RuntimeError("Qualification cannot run with Python assertion removal")
    package, fixture = args.package.resolve(strict=True), args.fixture.resolve(strict=True)
    scratch = args.scratch.resolve()
    scratch.mkdir(parents=True, exist_ok=False)  # Never reuse or delete user data.
    feed, tool, target = (scratch / n for n in ("feed", "tool", "fixture"))
    feed.mkdir()
    target.mkdir()
    shutil.copy2(package, feed / package.name)
    previous = args.upgrade_from.resolve(strict=True) if args.upgrade_from else None
    previous_version = None
    if previous:
        with zipfile.ZipFile(previous) as archive:
            specs = [name for name in archive.namelist() if name.endswith(".nuspec")]
            assert len(specs) == 1
            metadata = ET.fromstring(archive.read(specs[0])).find("{*}metadata")
            assert metadata.findtext("{*}id") == "CodeAtlas.Tool"
            previous_version = metadata.findtext("{*}version")
            assert tuple(map(int, previous_version.split("."))) < tuple(map(int, args.version.split(".")))
        shutil.copy2(previous, feed / previous.name)
    with zipfile.ZipFile(fixture) as archive:
        for name in archive.namelist():
            destination = (target / name).resolve()
            if not destination.is_relative_to(target) or "\\" in name:
                raise ValueError("Unsafe fixture archive entry")
        archive.extractall(target)
    assert (target / "Directory.Build.props").is_file()
    assert (target / "Directory.Build.targets").is_file()
    assert not list(target.rglob("CodeAtlas*.csproj")), "Fixture must not include the product source"
    assert not list(target.rglob("project.assets.json")), "Fixture must not reuse restored assets"
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("Install the SDK pinned in the fixture global.json")
    dotnet = str(Path(dotnet).resolve())
    # Override NuGet paths and ambient MSBuild selection. Preserve other host
    # environment, but use the SDK behind the resolved dotnet executable.
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("MSBUILD", "NUGET_", "DOTNET_"))}
    env.update({"DOTNET_ROOT": str(Path(dotnet).parent),
                "DOTNET_CLI_HOME": str(scratch / "dotnet-home"),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "DOTNET_NOLOGO": "1",
                "NUGET_PACKAGES": str(scratch / "nuget-packages"),
                "NUGET_HTTP_CACHE_PATH": str(scratch / "http-cache"),
                "NUGET_PLUGINS_CACHE_PATH": str(scratch / "plugin-cache"),
                "NUGET_SCRATCH": str(scratch / "nuget-scratch")})
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", {"key": "qualified-local-package", "value": str(feed)})
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    config_path = target / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    results = []

    def run(label, command, expected=0, parse=False, stdin=None):
        command = [str(x) for x in command]
        step = {"step": label, "command": command, "state": "running"}
        results.append(step)
        evidence = scratch / "steps.json"
        evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
        stdout_path, stderr_path = (scratch / (label + suffix) for suffix in (".stdout.txt", ".stderr.txt"))
        with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
            try:
                process = subprocess.run(command, cwd=target, env=env, input=stdin,
                                         stdout=stdout, stderr=stderr, timeout=300)
            except Exception as error:
                step.update(state="failed", error=type(error).__name__)
                evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
                raise
        step.update(exitCode=process.returncode, state="passed" if process.returncode == expected else "failed")
        evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
        assert process.returncode == expected, f"{label} failed; inspect {scratch}"
        process.stdout = stdout_path.read_text(encoding="utf-8-sig")
        process.stderr = stderr_path.read_text(encoding="utf-8-sig")
        return json.loads(process.stdout) if parse else process

    run("sdk-info", [dotnet, "--info"])
    product_package_version = ".".join(str(int(v)) for v in args.version.split("."))
    executable = tool / ("codeatlas.exe" if os.name == "nt" else "codeatlas")
    if previous:
        run("install-previous", [dotnet, "tool", "install", "CodeAtlas.Tool", "--tool-path", tool,
                                "--version", previous_version, "--configfile", config_path])
        old_version = run("previous-version", [executable, "--version"]).stdout.strip()
        assert tuple(map(int, old_version.split("."))) == tuple(map(int, previous_version.split(".")))
        run("upgrade", [dotnet, "tool", "update", "CodeAtlas.Tool", "--tool-path", tool,
                        "--version", product_package_version, "--configfile", config_path])
    else:
        run("install", [dotnet, "tool", "install", "CodeAtlas.Tool", "--tool-path", tool,
                        "--version", product_package_version, "--configfile", config_path])
    assert executable.is_file()
    assert run("version", [executable, "--version"]).stdout.strip() == args.version
    assert "codeatlas" in run("help", [executable, "--help"]).stdout
    invalid = run("invalid-arguments", [executable, "unknown"], expected=2)
    assert json.loads(invalid.stderr)["error"]["code"] == "invalid_arguments"
    run("restore-fixture", [dotnet, "restore", "Sample.slnx", "--locked-mode",
                            "--configfile", config_path])
    index = run("index", [executable, "index", "--solution", "Sample.slnx"], parse=True)
    assert index["toolVersion"] == args.version
    assert (index["projects"], index["types"], index["methods"]) == (2, 2, 6)
    index_path = Path(index["index"])
    assert index_path.resolve().is_relative_to(target)
    status = run("status", [executable, "index-status", "--index", index_path], parse=True)
    assert status["freshness"]["isStale"] is False
    symbols = run("symbols", [executable, "find-symbol", "--index", index_path,
                              "--name", "Calculate"], parse=True)
    int_id = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)"
    string_id = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.String)"
    assert {s["id"] for s in symbols["symbols"]} == {int_id, string_id}
    refs = run("references", [executable, "find-references", "--index", index_path,
                              "--symbol", int_id], parse=True)["references"]
    assert len(refs) == 3
    assert sorted(r["kind"] for r in refs) == ["invocation", "invocation", "reference"]
    assert {r["definitionId"] for r in refs} == {int_id}
    # A single stemmed token on this six-method corpus is below the evidence gate.
    # The same abstention happens from a checkout build, so it is not an install defect.
    calculate = run("search-calculate", [executable, "search-symbols", "--index", index_path,
                                        "--query", "Calculate", "--mode", "names"], parse=True)
    assert calculate["success"] is True and calculate["mode"] == "names"
    assert calculate["decision"] == "abstained-insufficient-evidence"
    assert calculate["symbols"] == [] and calculate["withheldWeakMatches"] == 2
    search = run("search", [executable, "search-symbols", "--index", index_path,
                            "--query", "LocalCaller", "--mode", "names"], parse=True)
    assert search["success"] is True and search["decision"] == "lexical-candidates"
    assert [item["symbol"]["id"] for item in search["symbols"]] == [
        "Library/Library.csproj::M:Sample.Calculator.LocalCaller"]
    mcp_input = "\n".join([
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "package-smoke", "version": "0"}}}),
        json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}),
        json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
            "name": "search_symbols",
            "arguments": {"workspace": ".", "query": "LocalCaller", "mode": "names"}}}),
        json.dumps({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {
            "name": "find_references",
            "arguments": {"workspace": ".",
                          "id": "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)",
                          "kinds": ["invocation"]}}}),
    ]) + "\n"
    mcp = run("mcp", [executable, "mcp", "--solution", "Sample.slnx"], stdin=mcp_input.encode())
    messages = [json.loads(line) for line in mcp.stdout.splitlines() if line.strip()]
    assert len(messages) == 4
    assert messages[0]["result"]["protocolVersion"] == "2025-06-18"
    assert messages[0]["result"]["capabilities"]["tools"]["listChanged"] is False
    assert messages[0]["result"]["serverInfo"] == {"name": "codeatlas", "version": args.version}
    assert [tool["name"] for tool in messages[1]["result"]["tools"]] == [
        "find_symbol", "search_symbols", "find_references", "index_status", "coverage", "index"]
    assert messages[2]["result"]["isError"] is False
    found = json.loads(messages[2]["result"]["content"][0]["text"])
    assert found["command"] == "search_symbols" and found["result"]["decision"] == "lexical-candidates"
    assert [item["symbol"]["id"] for item in found["result"]["symbols"]] == [
        "Library/Library.csproj::M:Sample.Calculator.LocalCaller"]
    assert messages[3]["result"]["isError"] is False
    uses = json.loads(messages[3]["result"]["content"][0]["text"])
    assert uses["command"] == "find_references"
    assert [(item["path"], item["line"], item["column"]) for item in uses["result"]["references"]] == [
        ("Caller/Consumer.cs", 7, 47), ("Library/Calculator.cs", 7, 40)]
    empty = run("empty", [executable, "find-symbol", "--index", index_path,
                          "--name", "DoesNotExist"], parse=True)
    assert empty["symbols"] == []
    source = target / "Library" / "Calculator.cs"
    source.write_text(source.read_text(encoding="utf-8") + "\n// package freshness smoke\n",
                      encoding="utf-8")
    stale = run("stale-query", [executable, "find-symbol", "--index", index_path,
                               "--name", "Calculate"], expected=3)
    assert json.loads(stale.stderr)["error"]["code"] == "stale_index"
    run("reindex", [executable, "index", "--solution", "Sample.slnx"], parse=True)
    fresh = run("refreshed-status", [executable, "index-status", "--index", index_path], parse=True)
    assert fresh["freshness"]["isStale"] is False
    report = {"scope": "isolated installed-tool public-fixture smoke",
              "platform": platform.platform(), "machine": platform.machine(),
              "productVersion": args.version,
              "upgradeFromVersion": previous_version,
              "upgradeFromPackageSha256": hashlib.sha256(previous.read_bytes()).hexdigest() if previous else None,
              "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
              "fixtureSha256": hashlib.sha256(fixture.read_bytes()).hexdigest(),
              "steps": results}
    (scratch / "smoke-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
