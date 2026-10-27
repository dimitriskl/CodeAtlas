"""Public agent benchmark: the same headless agent and model on each task in three modes.

Modes: plain (built-in file, search and shell tools), lsp (plus the Claude Code LSP tool
backed by csharp-ls) and codeatlas (plus the CodeAtlas CLI and a short instruction).
The target repository is pinned in target.json and tasks are data files in tasks/.

  prepare   clone the pinned target outside this repository, create one worktree per
            mode, restore and build it, and index the codeatlas worktree (no model calls)
  run       run the agent; --dry-run validates tasks, checkouts and commands without
            calling a model. A paid run also requires --confirm-paid-run.
            --provider ollama runs the same agent against a local Ollama model instead
            (no Anthropic key is passed, cost is recorded as 0).
  rescore   re-score stored find/impact runs with the current answer parser (no model calls);
            writes rescore.json and rescore.md next to each results.json

Answers are parsed tolerantly (JSON object or list, double-encoded or escaped JSON, markdown
fences, Windows paths, or paths and names extracted from text); the content checks stay strict.

Each run resets its worktree to the pinned commit before and after the agent works.
Results (JSON plus a markdown summary) go to benchmarks/agent/results/<stamp>-<label>/.
"""
import argparse
import datetime
import fnmatch
import json
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parent
MODES = ("plain", "lsp", "codeatlas")
KINDS = ("find", "impact", "change")
ANSWER_FILE = "codeatlas-bench-answer.json"
BASE_TOOLS = "Bash,Read,Edit,Write,Glob,Grep"
DEFAULT_BENCH_ROOT = Path(r"C:\temp\codeatlas-bench") if os.name == "nt" else Path.home() / "codeatlas-bench"
# Variables the CodeAtlas wrapper keeps. Values are captured once by prepare, so the
# index and every agent query see the same environment. No credentials are listed.
WRAPPER_ENVIRONMENT = (
    "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "PATH", "TEMP", "TMP", "USERPROFILE",
    "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles",
    "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
    "CommonProgramW6432", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "NUMBER_OF_PROCESSORS",
    "PROCESSOR_ARCHITECTURE", "OS", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "NUGET_PACKAGES")
FIXED_WRAPPER_ENVIRONMENT = {"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}
# The agent process receives only these variables (plus DOTNET_* and the one Anthropic
# credential). Other provider keys, tokens and parent-session variables are not passed.
AGENT_ENVIRONMENT = (
    "PATH", "PATHEXT", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
    "SystemRoot", "SystemDrive", "windir", "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
    "ProgramData", "CommonProgramFiles", "CommonProgramFiles(x86)", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    "OS", "USERNAME", "COMPUTERNAME", "NUGET_PACKAGES", "CLAUDE_CODE_GIT_BASH_PATH")
AGENT_CREDENTIAL = "ANTHROPIC_API_KEY"
PROVIDERS = ("anthropic", "ollama")
DEFAULT_OLLAMA_URL = "http://localhost:11434"
# Ollama serves the Anthropic Messages API. The token is a placeholder that Ollama ignores;
# the Anthropic key is never passed in this mode, so a misrouted request cannot be billed.
OLLAMA_AUTH_TOKEN = "ollama"
# Every model alias Claude Code may use (main, background and subagent) points at the local model.
OLLAMA_MODEL_VARIABLES = ("ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
                          "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL")
# Smallest context that holds Claude Code's system prompt and tool definitions with room to work.
OLLAMA_MIN_CONTEXT = 16384
# Harness-owned settings; the cloned repository's .claude settings are never loaded.
AGENT_SETTINGS = HERE / "agent-settings.json"
ALLOWED_BASH = ("dotnet build", "dotnet test", "git diff", "git status")
# Task suites. jellyfin is the pinned public target; small is a local 2-project solution sized
# for small local models. Each suite keeps its own clone, worktrees and CodeAtlas index.
SUITES = {
    "jellyfin": {"target": HERE / "target.json", "tasks": HERE / "tasks", "directory": None},
    "small": {"target": HERE / "target-small.json", "tasks": HERE / "tasks-small", "directory": Path("suites") / "small"},
}
# A local suite's repository is created by prepare with this fixed identity and date, so the
# commit hash pinned in its target file is reproducible.
LOCAL_COMMIT_ENVIRONMENT = {"GIT_AUTHOR_NAME": "CodeAtlas benchmark", "GIT_AUTHOR_EMAIL": "benchmark@codeatlas.invalid",
                            "GIT_COMMITTER_NAME": "CodeAtlas benchmark",
                            "GIT_COMMITTER_EMAIL": "benchmark@codeatlas.invalid",
                            "GIT_AUTHOR_DATE": "2026-01-01T00:00:00+00:00", "GIT_COMMITTER_DATE": "2026-01-01T00:00:00+00:00"}


# ---------------------------------------------------------------- data validation

def load_target(path):
    target = json.loads(Path(path).read_text(encoding="utf-8"))
    for field in ("name", "commit", "solution", "why"):
        if not isinstance(target.get(field), str) or not target[field]:
            raise ValueError(f"target.{field} is required")
    if ("repository" in target) == ("source" in target):
        raise ValueError("target needs exactly one of repository (a git URL) or source (a directory in this folder)")
    if "source" in target and not (Path(path).parent / target["source"]).is_dir():
        raise ValueError(f"target.source not found: {target['source']}")
    target.setdefault("repository", f"local source benchmarks/agent/{target.get('source')}")
    if not re.fullmatch(r"[0-9a-f]{40}", target["commit"]):
        raise ValueError("target.commit must be a full 40-character commit hash")
    return target


def suite_root(bench_root, suite):
    directory = SUITES[suite]["directory"]
    return Path(bench_root) / directory if directory else Path(bench_root)


def validate_task(task, directory=HERE / "tasks"):
    if not isinstance(task, dict):
        raise ValueError("Task must be an object")
    identity = task.get("id")
    if not isinstance(identity, str) or not re.fullmatch(r"[a-z0-9-]+", identity):
        raise ValueError("Task id must contain lowercase letters, digits and hyphens")
    if task.get("kind") not in KINDS:
        raise ValueError(f"{identity}: kind must be one of {KINDS}")
    if not isinstance(task.get("prompt"), str) or len(task["prompt"]) < 20:
        raise ValueError(f"{identity}: prompt is missing")
    if not isinstance(task.get("answer"), bool):
        raise ValueError(f"{identity}: answer must be true or false")
    checks = task.get("checks")
    if not isinstance(checks, list) or not checks:
        raise ValueError(f"{identity}: at least one check is required")
    for check in checks:
        kind = check.get("type") if isinstance(check, dict) else None
        if kind in ("answer-set", "answer-contains"):
            if not task["answer"]:
                raise ValueError(f"{identity}: answer checks need answer: true")
            if check.get("field") not in ("files", "symbols"):
                raise ValueError(f"{identity}: answer check field must be files or symbols")
            if kind == "answer-set":
                if check.get("match") not in ("path", "name") or not check.get("required"):
                    raise ValueError(f"{identity}: answer-set needs match and required")
                for name in ("minRecall", "minPrecision"):
                    value = check.get(name, 1.0 if name == "minRecall" else 0.0)
                    if type(value) not in (int, float) or not 0 <= value <= 1:
                        raise ValueError(f"{identity}: {name} must be between 0 and 1")
            elif not check.get("groups") or not all(isinstance(g, list) and g for g in check["groups"]):
                raise ValueError(f"{identity}: answer-contains needs non-empty groups")
        elif kind == "regex":
            if bool(check.get("path")) == bool(check.get("glob")):
                raise ValueError(f"{identity}: regex check needs exactly one of path or glob")
            if check.get("expect") not in ("present", "absent") or not isinstance(check.get("pristine"), bool):
                raise ValueError(f"{identity}: regex check needs expect and pristine")
            re.compile(check.get("pattern", ""))
        elif kind == "command":
            if not isinstance(check.get("argv"), list) or not check["argv"] or not check.get("name"):
                raise ValueError(f"{identity}: command check needs name and argv")
        else:
            raise ValueError(f"{identity}: unknown check type {kind!r}")
    reference = task.get("reference")
    if not isinstance(reference, dict) or ("answer" in reference) == ("patch" in reference):
        raise ValueError(f"{identity}: reference needs exactly one of answer or patch")
    if "patch" in reference and not (Path(directory) / reference["patch"]).is_file():
        raise ValueError(f"{identity}: reference patch not found: {reference['patch']}")
    if task["answer"] != ("answer" in reference):
        raise ValueError(f"{identity}: answer tasks use a reference answer, change tasks a reference patch")
    return task


def load_tasks(directory=HERE / "tasks", selected=None):
    tasks = [validate_task(json.loads(path.read_text(encoding="utf-8")), directory)
             for path in sorted(Path(directory).glob("*.json"))]
    identities = [task["id"] for task in tasks]
    if len(set(identities)) != len(identities):
        raise ValueError("Task ids must be unique")
    if selected:
        unknown = sorted(set(selected) - set(identities))
        if unknown:
            raise ValueError("Unknown task ids: " + ", ".join(unknown))
        tasks = [task for task in tasks if task["id"] in selected]
    return tasks


# ---------------------------------------------------------------- checks

QUOTES = "`'\" *"


def strip_root(value, root):
    text = str(value).replace("\\", "/")
    prefix = str(root).replace("\\", "/").rstrip("/") + "/"
    return text[len(prefix):] if root and text.lower().startswith(prefix.lower()) else text


def normalize_path(value, root=None):
    """Repository-relative, forward slashes, lower case: absolute (under root), ./ and \\ forms compare equal."""
    text = re.sub(r"/{2,}", "/", str(value).strip().strip(QUOTES).replace("\\", "/"))
    if re.match(r"/[A-Za-z]:/", text):
        text = text[1:]
    text = strip_root(text, root)
    while text.startswith("./"):
        text = text[2:]
    return text.lower()


def normalize_symbol(value):
    """Dotted symbol without parameter list, generic arguments or arity, doc-ID prefix, or project prefix.

    Nested-type (+), member (::, #, :) and path separators become dots, so Ns.Type+Member, Type::Member and
    Project/Shop.csproj::M:Ns.Type.Member(System.String) all normalize to a dotted name ending in Type.Member."""
    text = str(value).strip().strip(QUOTES)
    text = re.sub(r"\(.*$", "", text)
    # A CodeAtlas symbol ID is <project>::<doc-ID>; "global::" is C#'s root alias; other "::" separate members.
    project = re.match(r"^.*\.(?:cs|vb|fs)proj::", text, re.IGNORECASE)
    text = text[project.end():] if project else re.sub(r"^global::", "", text)
    text = re.sub(r"^[A-Z]:(?=[A-Za-z_])", "", text.strip())
    previous = None
    while previous != text:
        previous, text = text, re.sub(r"<[^<>]*>", "", text)
    text = re.sub(r"`\d+", "", text)
    text = re.sub(r"^global\.", "", re.sub(r"[+#:/\\]", ".", text))
    return re.sub(r"\.{2,}", ".", text).strip(". ")


def simple_name(value):
    return normalize_symbol(value).split(".")[-1].strip()


def evaluate_answer(check, answer, root=None):
    values = answer.get(check["field"]) if isinstance(answer, dict) else None
    if not isinstance(values, list):
        return {"passed": False, "detail": f"answer.{check['field']} is not a list"}
    values = [str(value) for value in values]
    if check["type"] == "answer-contains":
        normal = (lambda value: normalize_path(value, root)) if check["field"] == "files" else normalize_symbol
        # A group is found when a pattern occurs in an entry as written or in its normalized form.
        missing = [group for group in check["groups"]
                   if not any(pattern in value or normal(pattern) in normal(value) for pattern in group for value in values)]
        return {"passed": not missing, "missingGroups": missing}
    key = (lambda value: normalize_path(value, root)) if check["match"] == "path" else simple_name
    given = {key(value) for value in values if key(value)}
    required = {key(value) for value in check["required"]}
    allowed = required | {key(value) for value in check.get("allowed", [])}
    forbidden = {key(value) for value in check.get("forbidden", [])}
    recall = len(given & required) / len(required)
    precision = len(given & allowed) / len(given) if given else 0.0
    hit_forbidden = sorted(given & forbidden)
    passed = (recall >= check.get("minRecall", 1.0) and precision >= check.get("minPrecision", 0.0)
              and not hit_forbidden)
    return {"passed": passed, "recall": round(recall, 4), "precision": round(precision, 4),
            "missing": sorted(required - given), "unexpected": sorted(given - allowed), "forbidden": hit_forbidden}


def candidate_files(root, pattern):
    root = Path(root)
    for directory, names, files in os.walk(root):
        names[:] = [name for name in names if name not in (".git", ".codeatlas", "bin", "obj", "node_modules")]
        for name in files:
            relative = (Path(directory) / name).relative_to(root).as_posix()
            if fnmatch.fnmatch(relative, pattern) or fnmatch.fnmatch(relative, pattern.removeprefix("**/")):
                yield relative


def evaluate_regex(check, root):
    pattern = re.compile(check["pattern"], re.MULTILINE)
    files = [check["path"]] if check.get("path") else sorted(candidate_files(root, check["glob"]))
    matches = []
    for relative in files:
        path = Path(root) / relative
        if not path.is_file():
            if check.get("path"):
                return {"passed": False, "detail": f"missing file {relative}"}
            continue
        if pattern.search(path.read_text(encoding="utf-8-sig", errors="replace")):
            matches.append(relative)
    present = bool(matches)
    return {"passed": present == (check["expect"] == "present"), "matches": matches[:20]}


def run_command_check(check, root, log):
    started = time.perf_counter()
    try:
        completed = subprocess.run(check["argv"], cwd=root, capture_output=True, text=True, errors="replace",
                                   timeout=check.get("timeoutSeconds", 900))
        output, code = completed.stdout + completed.stderr, completed.returncode
    except subprocess.TimeoutExpired as error:
        output, code = f"timeout after {error.timeout} s", None
    if log is not None:
        Path(log).write_text(output, encoding="utf-8")
    return {"passed": code == 0, "exitCode": code, "seconds": round(time.perf_counter() - started, 2)}


def evaluate_checks(task, root, answer, log_directory=None, run_commands=True):
    results = []
    for check in task["checks"]:
        if check["type"].startswith("answer-"):
            result = evaluate_answer(check, answer, root)
        elif check["type"] == "regex":
            result = evaluate_regex(check, root)
        elif run_commands:
            log = Path(log_directory) / f"check-{check['name']}.log" if log_directory else None
            result = run_command_check(check, root, log)
        else:
            continue
        results.append({"type": check["type"], "name": check.get("name") or check.get("path") or check.get("glob")
                        or check.get("field"), **result})
    return results


# ---------------------------------------------------------------- prompts and commands

def build_prompt(target, task, compact=False):
    if compact:
        template = (HERE / "prompts" / "task-compact.md").read_text(encoding="utf-8")
        if task["answer"]:
            rule = ("When done, use the Write tool to create " + ANSWER_FILE + " in the current directory: "
                    '{"files": ["path/from/repository/root.cs"], "symbols": ["Type.Member"]}.')
        else:
            rule = f"Edit the files in place, then check with: dotnet build {target['solution']}"
        return template.format(target_name=target["name"], solution=target["solution"], task_prompt=task["prompt"],
                               answer_rule=rule).strip() + "\n"
    template = (HERE / "prompts" / "task.md").read_text(encoding="utf-8")
    if task["answer"]:
        rule = ("- When you are done, write your answer as JSON to " + ANSWER_FILE + " in the repository root: "
                '{"files": [repository-relative paths with forward slashes], "symbols": [Type.Member or type names]}. '
                "List only what the task asks for.")
    else:
        rule = "- Leave your change in the working tree. It is checked by building the solution and inspecting the source."
    return template.format(target_name=target["name"], repository=target["repository"], commit=target["commit"],
                           solution=target["solution"], task_prompt=task["prompt"], answer_rule=rule).strip() + "\n"


def mode_instruction(mode, codeatlas=None, compact=False):
    if mode == "plain":
        return None
    compact_file = HERE / "prompts" / f"{mode}-compact.md"
    text = (compact_file if compact and compact_file.is_file() else HERE / "prompts" / f"{mode}.md").read_text(encoding="utf-8")
    if mode == "codeatlas":
        if not codeatlas:
            raise ValueError("CodeAtlas setup is missing; run prepare first")
        text = text.format(cli=codeatlas_cli(codeatlas), manifest=Path(codeatlas["manifest"]).as_posix(),
                           overview=Path(codeatlas["overview"]).as_posix(), solution_path=Path(codeatlas["solution"]).as_posix())
    return text.strip()


def quote(path):
    text = Path(path).as_posix()
    return f'"{text}"' if " " in text else text


def codeatlas_cli(setup):
    """The exact command prefix the agent uses for CodeAtlas; also its permission rule."""
    return f"{quote(sys.executable)} {quote(setup['wrapper'])}"


def allowed_tools(mode, cli=None):
    rules = ["Read", "Grep", "Glob", "Edit", "Write"] + [f"Bash({prefix}:*)" for prefix in ALLOWED_BASH]
    if mode == "lsp":
        rules.append("LSP")
    if mode == "codeatlas" and cli:
        rules.append(f"Bash({cli}:*)")
    return rules


def compact_tools(mode, kind):
    """The smallest tool set for a task: fewer tool definitions in a small model's context."""
    tools = ["Read", "Grep", "Glob"] + (["Edit", "Bash"] if kind == "change" else ["Write"])
    if mode == "codeatlas" and "Bash" not in tools:
        tools.append("Bash")
    if mode == "lsp":
        tools.append("LSP")
    return ",".join(tools)


def compact_system_prompt(instruction=None, root="."):
    # Claude Code's default prompt states the working directory; the replacement must too.
    text = (HERE / "prompts" / "system-compact.md").read_text(encoding="utf-8").strip()
    text = text.format(root=str(root), root_example=str(Path(root) / "Folder" / "File.cs"))
    return text + ("\n\n" + instruction if instruction else "")


def claude_command(claude, mode, model, max_budget_usd, instruction=None, cli=None, unsafe_bypass_permissions=False,
                   effort=None, extra=(), max_turns=None, compact_kind=None, root="."):
    """compact_kind (the task kind) switches to the compact agent: a short replacement system prompt
    and the minimal tool set. Permissions and isolation are the same as the default agent."""
    tools = compact_tools(mode, compact_kind) if compact_kind else BASE_TOOLS + (",LSP" if mode == "lsp" else "")
    command = [claude, "-p", "--output-format", "stream-json", "--verbose", "--model", model,
               "--no-session-persistence", "--setting-sources", "", "--settings", str(AGENT_SETTINGS),
               "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}', "--disable-slash-commands",
               "--tools", tools]
    if max_budget_usd is not None:
        # Omitted for local models: Claude Code prices unknown models as if they were billed.
        command += ["--max-budget-usd", f"{max_budget_usd:g}"]
    if unsafe_bypass_permissions:
        command += ["--permission-mode", "bypassPermissions"]
    else:
        # Anything not allowed here is denied without a prompt.
        command += ["--permission-mode", "dontAsk", "--allowedTools", *allowed_tools(mode, cli)]
    if effort:
        command += ["--effort", effort]
    if max_turns:
        command += ["--max-turns", str(max_turns)]
    if compact_kind:
        command += ["--system-prompt", compact_system_prompt(instruction, root)]
    elif instruction:
        command += ["--append-system-prompt", instruction]
    if mode == "lsp":
        command += ["--plugin-dir", str(HERE / "lsp-plugin")]
    return command + list(extra)


def agent_environment(mode, source=None, provider="anthropic", ollama_url=DEFAULT_OLLAMA_URL, model=None,
                      config_dir=None):
    source = os.environ if source is None else source
    credentials = (AGENT_CREDENTIAL,) if provider == "anthropic" else ()
    environment = {name: source[name] for name in AGENT_ENVIRONMENT + credentials if name in source}
    environment.update({name: value for name, value in source.items()
                        if name.upper().startswith("DOTNET_") and not re.search("KEY|TOKEN|SECRET|PASSWORD", name.upper())})
    environment.update({"CLAUDE_CODE_DISABLE_AUTO_MEMORY": "1", "DISABLE_AUTOUPDATER": "1"})
    if provider == "ollama":
        # No Anthropic key: requests go to the local Ollama server with a placeholder token.
        environment.update({"ANTHROPIC_BASE_URL": ollama_url.rstrip("/"), "ANTHROPIC_AUTH_TOKEN": OLLAMA_AUTH_TOKEN,
                            "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1"})
        if model:
            environment.update({name: model for name in OLLAMA_MODEL_VARIABLES})
        if config_dir:
            # An empty harness-owned Claude Code home: the user's /login credentials are not visible either.
            environment["CLAUDE_CONFIG_DIR"] = str(config_dir)
    if mode == "lsp":
        environment["ENABLE_LSP_TOOL"] = "1"
    return environment


def ollama_get(url, path, payload=None, timeout=30):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(url.rstrip("/") + path, data=data,
                                     headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def ollama_status(url, model, fetch=ollama_get):
    """Checks the Ollama endpoint and the model without generating text. Raises RuntimeError on failure."""
    try:
        version = fetch(url, "/api/version").get("version")
        names = {item.get(key) for item in fetch(url, "/api/tags").get("models") or [] for key in ("name", "model")}
    except (OSError, ValueError) as error:
        raise RuntimeError(f"Ollama is not reachable at {url}: {error}") from error
    wanted = model if ":" in model else model + ":latest"
    if model not in names and wanted not in names:
        available = ", ".join(sorted(name for name in names if name)) or "none"
        raise RuntimeError(f"Ollama model {model!r} not found at {url} (available: {available}). "
                           "Create it locally or pick an installed model; the harness never pulls models.")
    try:
        details = fetch(url, "/api/show", {"model": model})
    except (OSError, ValueError) as error:
        raise RuntimeError(f"Ollama could not describe model {model!r}: {error}") from error
    match = re.search(r"^\s*num_ctx\s+(\d+)", details.get("parameters") or "", re.MULTILINE)
    context = int(match.group(1)) if match else None
    model_info = details.get("model_info") or {}
    maximum = next((value for key, value in model_info.items() if key.endswith(".context_length")), None)
    warning = None
    if not context or context < OLLAMA_MIN_CONTEXT:
        warning = (f"num_ctx is {context or 'the server default'}; Claude Code's system prompt and tools need at least "
                   f"{OLLAMA_MIN_CONTEXT} tokens. Create a derived model with PARAMETER num_ctx 32768 (see README).")
    return {"url": url.rstrip("/"), "version": version, "model": model, "numCtx": context,
            "modelContextLength": maximum, "capabilities": details.get("capabilities"), "warning": warning}


# ---------------------------------------------------------------- agent output

def classify_bash(command):
    text = command.lower()
    if "codeatlas" in text:
        return "codeatlas"
    if re.search(r"(^|[|;&\s])(grep|rg|findstr|select-string|git grep)\b", text):
        return "search"
    if re.search(r"(^|[|;&\s])dotnet\s+(build|test)\b", text):
        return "build"
    return "other"


def parse_stream(lines):
    """Metrics from claude -p --output-format stream-json --verbose output lines."""
    metrics = {"tools": {}, "bash": {}, "initTools": None, "model": None, "apiKeySource": None, "result": None}
    message_usage = {}
    for line in lines:
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if event.get("type") == "system" and event.get("subtype") == "init":
            metrics["initTools"] = event.get("tools")
            metrics["model"] = event.get("model")
            metrics["apiKeySource"] = event.get("apiKeySource")
        elif event.get("type") == "assistant":
            message = event.get("message") or {}
            if isinstance(message.get("usage"), dict):
                message_usage[message.get("id") or len(message_usage)] = message["usage"]
            for block in message.get("content") or []:
                if isinstance(block, dict) and block.get("type") == "tool_use":
                    name = block.get("name", "?")
                    metrics["tools"][name] = metrics["tools"].get(name, 0) + 1
                    if name == "Bash":
                        kind = classify_bash(str((block.get("input") or {}).get("command", "")))
                        metrics["bash"][kind] = metrics["bash"].get(kind, 0) + 1
        elif event.get("type") == "result":
            metrics["result"] = event
    result = metrics.pop("result") or {}
    usage_by_model = result.get("modelUsage") or {}
    if usage_by_model:
        totals = {key: sum(int(item.get(key) or 0) for item in usage_by_model.values())
                  for key in ("inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens")}
    else:
        # Without a result event (timeout or crash) the usage reported on each assistant message is summed.
        usages = [result.get("usage") or {}] if result else list(message_usage.values())
        totals = {name: sum(int(usage.get(key) or 0) for usage in usages) for name, key in (
            ("inputTokens", "input_tokens"), ("outputTokens", "output_tokens"),
            ("cacheReadInputTokens", "cache_read_input_tokens"), ("cacheCreationInputTokens", "cache_creation_input_tokens"))}
    totals["totalTokens"] = sum(totals.values())
    metrics.update(totals)
    metrics.update({
        "resultReceived": bool(result), "subtype": result.get("subtype"), "isError": result.get("is_error"),
        "usageSource": "result" if result else ("assistant-messages" if message_usage else None),
        "turns": result.get("num_turns") if result else (len(message_usage) or None),
        "costUsd": result.get("total_cost_usd"),
        "durationSeconds": (result.get("duration_ms") or 0) / 1000, "apiSeconds": (result.get("duration_api_ms") or 0) / 1000,
        "models": sorted(usage_by_model), "finalText": (result.get("result") or "")[-4000:]})
    return metrics


# ---------------------------------------------------------------- answer parsing
# Small models often write the answer in a broken format (escaped quotes, Windows backslashes, a bare
# list). Parsing is tolerant; the checks on the content are not. Every answer records how it was parsed.

FIELD_ALIASES = {"files": ("files", "file", "paths", "path"), "symbols": ("symbols", "symbol")}
STRICT_PARSES = {("file", "json-object"), ("final-text", "markdown-fence+json-object")}
PATH_EXTENSIONS = "cs|csproj|vb|fs|razor|cshtml|xaml|json|xml|props|targets|config|sln|slnx"
TEXT_PATH = re.compile(r"(?<![\w.\-/\\])((?:/?[A-Za-z]:[\\/]+)?(?:[\w.\-]+[\\/]+)*[\w\-][\w.\-]*\.(?:"
                       + PATH_EXTENSIONS + r"))(?![\w\-]|\.\w)", re.IGNORECASE)
TEXT_DOTTED = re.compile(r"(?<![\w.`])([A-Za-z_]\w*(?:(?:\.|::|\+)[A-Za-z_]\w*)+(?:<[^<>\s]*>)?)")
TEXT_MARKED = re.compile(r"`([A-Za-z_][\w.+:<>,`]*(?:\([^()`\n]*\))?)`|\*\*([A-Za-z_][\w.+:<>]*)\*\*")
JSON_STRING = re.compile(r'"(?:[^"\\]|\\.)*"', re.DOTALL)


def _outside_strings(text, replace):
    """Applies replace to the parts of text that are not inside JSON string literals."""
    parts, last = [], 0
    for match in JSON_STRING.finditer(text):
        parts += [replace(text[last:match.start()]), match.group(0)]
        last = match.end()
    return "".join(parts + [replace(text[last:])])


def _repair_layout(text):
    # Literal \n, \r and \t written between JSON tokens instead of real whitespace.
    return _outside_strings(text, lambda part: re.sub(r"\\[nrt]", " ", part))


def _repair_quotes(text):
    # The JSON text of a string literal written without its outer quotes: {\"files\": [\"A.cs\"]}.
    return text.replace('\\"', '"')


def _repair_backslashes(text):
    # Windows paths with single backslashes inside strings: every backslash is literal except \" and \\.
    return re.sub(r'\\\\|\\(?!")', lambda match: "\\\\" if match.group(0) == "\\" else match.group(0), text)


REPAIRS = ((), ("layout",), ("double-encoded",), ("escaped-quotes",), ("backslashes",), ("layout", "backslashes"),
           ("escaped-quotes", "backslashes"), ("escaped-quotes", "layout", "backslashes"))


def _decode(text, repairs):
    if repairs == ("double-encoded",):
        if '\\"' not in text:
            return None
        text = '"' + text + '"'
    for name in repairs:
        text = {"layout": _repair_layout, "escaped-quotes": _repair_quotes, "backslashes": _repair_backslashes,
                "double-encoded": lambda value: value}[name](text)
    try:
        return json.loads(text)
    except (json.JSONDecodeError, RecursionError):
        return None


def _is_path(value):
    text = str(value).strip()
    if "::" in text or "(" in text:
        return False
    return "/" in text or "\\" in text or bool(re.search(r"\.(?:" + PATH_EXTENSIONS + r")$", text, re.IGNORECASE))


def _string_list(value):
    if isinstance(value, str):
        value = [value]
    if not isinstance(value, list):
        return None
    return [str(item).strip() for item in value if isinstance(item, (str, int, float)) and str(item).strip()]


def coerce_answer(value):
    """{"files", "symbols"} from a decoded JSON value, or (None, None). Returns (answer, shape)."""
    if isinstance(value, dict):
        keys = {str(key).strip().lower(): key for key in value}
        answer = {}
        for field, aliases in FIELD_ALIASES.items():
            key = next((keys[alias] for alias in aliases if alias in keys), None)
            items = _string_list(value[key]) if key is not None else None
            if items is not None:
                answer[field] = items
        if answer:
            return answer, "json-object"
        nested = [item for item in value.values() if isinstance(item, (dict, list))]
        if len(nested) == 1:
            answer, shape = coerce_answer(nested[0])
            return (answer, "nested-" + shape) if answer else (None, None)
        return None, None
    if isinstance(value, list) and value and all(isinstance(item, dict) for item in value):
        parts = [coerce_answer(item)[0] or {} for item in value]
        answer = {field: [entry for part in parts for entry in part.get(field, [])]
                  for field in FIELD_ALIASES if any(field in part for part in parts)}
        return (answer, "json-list") if answer else (None, None)
    items = _string_list(value) if isinstance(value, list) else None
    if items and len(items) == len(value):
        return {"files": [item for item in items if _is_path(item)],
                "symbols": [item for item in items if not _is_path(item)]}, "json-list"
    return None, None


def _json_candidates(text):
    yield (), text
    for block in reversed(re.findall(r"```[\w-]*\s*\n?(.*?)```", text, re.DOTALL)):
        yield ("markdown-fence",), block.strip()
    for opening, closing in (("{", "}"), ("[", "]")):
        start, end = text.find(opening), text.rfind(closing)
        if 0 <= start < end and (start, end) != (0, len(text) - 1):
            yield ("embedded-json",), text[start:end + 1]


def parse_json_answer(text):
    """(answer, parse label) for JSON in any tolerated encoding, or (None, None)."""
    text = (text or "").lstrip("﻿").strip()
    if not text:
        return None, None
    for location, candidate in _json_candidates(text):
        for repairs in REPAIRS:
            value = _decode(candidate, repairs)
            if value is None:
                continue
            steps = list(location) + list(repairs)
            for _ in range(2):
                if not isinstance(value, str):
                    break
                decoded = _decode(value.strip(), ())
                if decoded is None:
                    decoded = next((item for item in (_decode(value.strip(), repair) for repair in REPAIRS[1:])
                                    if item is not None), None)
                if decoded is None:
                    break
                value = decoded
                if "double-encoded" not in steps:
                    steps.append("double-encoded")
            answer, shape = coerce_answer(value)
            if answer:
                return answer, "+".join(steps + [shape])
    return None, None


def parse_text_answer(text):
    """Fallback for prose: repository paths and symbol-like names mentioned in the text."""
    text = text or ""
    files, symbols = [], []
    for match in TEXT_PATH.finditer(text):
        path = match.group(1)
        if Path(path.replace("\\", "/")).name.lower() != ANSWER_FILE and path not in files:
            files.append(path)
    rest = TEXT_PATH.sub(" ", text)
    for match in list(TEXT_MARKED.finditer(rest)) + list(TEXT_DOTTED.finditer(rest)):
        name = next(group for group in match.groups() if group)
        # Symbols are PascalCase; all-lower tokens are prose ("e.g") or commands.
        if re.search(r"[A-Z]", name) and name not in symbols:
            symbols.append(name)
    return ({"files": files, "symbols": symbols}, "text-fallback") if files or symbols else (None, None)


def extract_answer(root, final_text, file_text=None):
    """(answer, source, parse) from the answer file, else the agent's final message.

    JSON in either place wins over text extraction; the answer file wins over the final message."""
    if file_text is None and root is not None and (Path(root) / ANSWER_FILE).is_file():
        file_text = (Path(root) / ANSWER_FILE).read_text(encoding="utf-8-sig", errors="replace")
    sources = (("file", file_text), ("final-text", final_text))
    for parser in (parse_json_answer, parse_text_answer):
        for source, text in sources:
            answer, parse = parser(text)
            if answer:
                return answer, source, parse
    return None, None, None


def is_lenient_parse(source, parse):
    """True when the answer would not have been read by the strict parser (JSON object in the file or a fence)."""
    return source is not None and (source, parse) not in STRICT_PARSES


def answer_file_from_stream(lines, mode=None):
    """Reconstructs the answer file a session wrote from its Write/Edit tool calls (successful ones only)."""
    calls, content = {}, None
    for line in lines:
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        message = event.get("message") if isinstance(event, dict) and isinstance(event.get("message"), dict) else {}
        for block in message.get("content") if isinstance(message.get("content"), list) else []:
            if not isinstance(block, dict):
                continue
            if event.get("type") == "assistant" and block.get("type") == "tool_use" and block.get("name") in ("Write", "Edit"):
                arguments = block.get("input") or {}
                path = str(arguments.get("file_path") or "").replace("\\", "/")
                parent = path.rsplit("/", 1)[0] if "/" in path else ""
                if path.rsplit("/", 1)[-1].lower() == ANSWER_FILE and (
                        not parent or parent in (".",) or (mode and parent.lower().endswith("/work/" + mode))):
                    calls[block.get("id")] = (block.get("name"), arguments)
            elif event.get("type") == "user" and block.get("type") == "tool_result" and block.get("tool_use_id") in calls:
                name, arguments = calls.pop(block["tool_use_id"])
                if block.get("is_error"):
                    continue
                if name == "Write":
                    content = str(arguments.get("content") or "")
                elif content is not None and arguments.get("old_string") in content:
                    old, new = str(arguments.get("old_string")), str(arguments.get("new_string") or "")
                    content = content.replace(old, new) if arguments.get("replace_all") else content.replace(old, new, 1)
    return content


# ---------------------------------------------------------------- reporting

def mean(values):
    values = [value for value in values if value is not None]
    return statistics.fmean(values) if values else None


def summarize(runs):
    summary = {"modes": {}, "tasks": {}, "relativeToPlain": {}}
    measured = [run for run in runs if run.get("status") == "completed"]
    for mode in MODES:
        selected = [run for run in measured if run["mode"] == mode]
        if not selected:
            continue
        summary["modes"][mode] = {
            "runs": len(selected), "successes": sum(run["success"] for run in selected),
            "successRate": sum(run["success"] for run in selected) / len(selected),
            "meanTotalTokens": mean(run["totalTokens"] for run in selected),
            "meanOutputTokens": mean(run["outputTokens"] for run in selected),
            "meanTurns": mean(run["turns"] for run in selected),
            "meanCostUsd": mean(run["costUsd"] for run in selected),
            "totalCostUsd": sum(run["costUsd"] or 0 for run in selected),
            "meanElapsedSeconds": mean(run["elapsedSeconds"] for run in selected),
            "lenientSuccesses": sum(bool(run["success"] and run.get("answerLenient")) for run in selected)}
    for run in measured:
        entry = summary["tasks"].setdefault(run["task"], {}).setdefault(run["mode"], [])
        entry.append(run)
    for task, by_mode in summary["tasks"].items():
        summary["tasks"][task] = {mode: {
            "runs": len(items), "successes": sum(item["success"] for item in items),
            "meanTotalTokens": mean(item["totalTokens"] for item in items),
            "meanTurns": mean(item["turns"] for item in items),
            "meanCostUsd": mean(item["costUsd"] for item in items),
            "meanElapsedSeconds": mean(item["elapsedSeconds"] for item in items)} for mode, items in by_mode.items()}
    for mode in ("lsp", "codeatlas"):
        paired = [values for values in summary["tasks"].values() if "plain" in values and mode in values]
        if not paired:
            continue
        relative = {}
        for metric in ("meanTotalTokens", "meanTurns", "meanCostUsd", "meanElapsedSeconds"):
            base = sum(values["plain"][metric] or 0 for values in paired)
            other = sum(values[mode][metric] or 0 for values in paired)
            relative[metric.removeprefix("mean")] = (other - base) / base if base else None
        relative["pairedTasks"] = len(paired)
        summary["relativeToPlain"][mode] = relative
    return summary


def format_number(value, digits=0, percent=False):
    if value is None:
        return "-"
    if percent:
        return f"{value * 100:+.0f}%"
    return f"{value:,.{digits}f}"


def render_markdown(report):
    summary = report["summary"]
    codeatlas = report.get("codeatlas") or {}
    lines = [f"# Agent benchmark: {report['label']}", "",
             f"- Target: {report['target']['name']} {report['target'].get('tag', '')} (`{report['target']['commit']}`)",
             f"- Agent: Claude Code {report.get('claudeVersion') or '?'}, provider {report.get('provider', 'anthropic')}, "
             f"model `{report['model']}`, "
             f"{report['repetitions']} repetition(s), started {report['startedAtUtc']}",
             f"- Suite: {report.get('suite', 'jellyfin')}; agent: "
             f"{'compact (short system prompt, minimal tools)' if report.get('compactAgent') else 'default Claude Code'}",
             f"- CodeAtlas: {codeatlas.get('version') or '-'}; upfront index "
             f"{format_number((codeatlas.get('setup') or {}).get('indexSeconds'), 1)} s, no model cost",
             f"- LSP mode: {(report.get('lsp') or {}).get('status', '-')}", "",
             "| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |",
             "| --- | --- | ---: | ---: | ---: | ---: | ---: |"]
    for mode, values in summary["modes"].items():
        lines.append(f"| {mode} | {values['successes']}/{values['runs']} | {format_number(values['meanTotalTokens'])} | "
                     f"{format_number(values['meanOutputTokens'])} | {format_number(values['meanTurns'], 1)} | "
                     f"{format_number(values['meanCostUsd'], 3)} | {format_number(values['meanElapsedSeconds'], 1)} |")
    if summary["relativeToPlain"]:
        lines += ["", "| Versus plain | Tokens | Turns | Cost | Time | Paired tasks |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
        for mode, values in summary["relativeToPlain"].items():
            lines.append(f"| {mode} | {format_number(values['TotalTokens'], percent=True)} | "
                         f"{format_number(values['Turns'], percent=True)} | {format_number(values['CostUsd'], percent=True)} | "
                         f"{format_number(values['ElapsedSeconds'], percent=True)} | {values['pairedTasks']} |")
    lines += ["", "| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |", "| --- | --- | --- | ---: | ---: | ---: |"]
    for task, by_mode in summary["tasks"].items():
        for mode, values in by_mode.items():
            lines.append(f"| {task} | {mode} | {values['successes']}/{values['runs']} | {format_number(values['meanTotalTokens'])} | "
                         f"{format_number(values['meanTurns'], 1)} | {format_number(values['meanCostUsd'], 3)} |")
    ollama = report.get("ollama")
    if ollama:
        lines.insert(5, f"- Ollama {ollama.get('version') or '?'} at {ollama.get('url')}: num_ctx "
                        f"{ollama.get('numCtx') or 'server default'}; local model, cost recorded as 0")
    parses = {}
    for run in report["runs"]:
        if run.get("status") == "completed" and run.get("kind") != "change":
            parses[run.get("answerParse") or "none"] = parses.get(run.get("answerParse") or "none", 0) + 1
    if parses:
        lenient = ", ".join(f"{mode} {values.get('lenientSuccesses', 0)}" for mode, values in summary["modes"].items())
        lines += ["", "Answer parsing (find/impact runs): " + ", ".join(f"{name} {count}" for name, count in sorted(parses.items()))
                  + f". Successes that needed a lenient parse: {lenient}."]
    skipped = [run for run in report["runs"] if run.get("status") != "completed"]
    if skipped:
        lines += ["", "Not measured: " + "; ".join(f"{run['task']}/{run['mode']}: {run.get('reason')}" for run in skipped)]
    lines += ["", "Tokens are input + cache creation + cache read + output across all models used by the session.",
              "Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds."]
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- environment helpers

def git(arguments, cwd, check=True):
    completed = subprocess.run(["git", *arguments], cwd=cwd, capture_output=True, text=True, errors="replace")
    if check and completed.returncode != 0:
        raise RuntimeError(f"git {' '.join(arguments)} failed in {cwd}: {completed.stderr.strip()}")
    return completed.stdout.strip()


def worktree_path(bench_root, mode):
    return Path(bench_root) / "work" / mode


def reset_worktree(root, commit):
    git(["reset", "--hard", "--quiet", commit], root)
    git(["clean", "-fd", "--quiet"], root)


def checkout_state(root, commit):
    if not (Path(root) / ".git").exists():
        return "missing"
    head = git(["rev-parse", "HEAD"], root, check=False)
    if head != commit:
        return f"wrong commit {head or '?'}"
    return "dirty" if git(["status", "--porcelain"], root, check=False) else "clean"


def tool_version(command):
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=60, errors="replace")
    except (OSError, subprocess.TimeoutExpired) as error:
        return None, str(error)
    return (completed.stdout.strip() or completed.stderr.strip()).splitlines()[0] if completed.returncode == 0 else None, \
        (completed.stderr or completed.stdout).strip()[:300]


def lsp_status():
    server = shutil.which("csharp-ls")
    if server is None:
        return {"available": False, "status": "unavailable: csharp-ls is not installed "
                "(dotnet tool install --global csharp-ls); the LSP mode is skipped"}
    return {"available": True, "status": f"csharp-ls at {server}; LSP tool use is verified per run from the init event",
            "server": server}


def codeatlas_setup(bench_root):
    path = Path(bench_root) / "tools" / "codeatlas-setup.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else None


def run_wrapper(setup, arguments, timeout=3600):
    completed = subprocess.run([sys.executable, setup["wrapper"], *arguments], capture_output=True, text=True,
                               errors="replace", timeout=timeout)
    try:
        payload = json.loads(completed.stdout) if completed.stdout.strip().startswith("{") else None
    except json.JSONDecodeError:
        payload = None
    return completed.returncode, payload, completed.stdout, completed.stderr


def index_freshness(setup):
    code, payload, _, stderr = run_wrapper(setup, ["index-status", "--index", setup["manifest"]], timeout=600)
    freshness = (payload or {}).get("freshness") or {}
    return {"exitCode": code, "state": freshness.get("state"), "isStale": freshness.get("isStale"),
            "changedFiles": (freshness.get("changedFiles") or [])[:10], "error": None if payload else stderr[-500:]}


# ---------------------------------------------------------------- prepare

def prepare(args, target):
    bench_root = Path(args.work_root)
    base = bench_root / target["name"]
    bench_root.mkdir(parents=True, exist_ok=True)
    if "source" in target:
        create_local_repository(base, HERE / target["source"], target["commit"])
    elif not (base / ".git").exists():
        print(f"Cloning {target['repository']} at {target['commit']} into {base}", flush=True)
        base.mkdir(parents=True)
        git(["init", "--quiet"], base)
        git(["remote", "add", "origin", target["repository"]], base)
        git(["fetch", "--quiet", "--depth", "1", "origin", target["commit"]], base)
        git(["checkout", "--quiet", "--detach", "FETCH_HEAD"], base)
    if "source" not in target and git(["rev-parse", "HEAD"], base) != target["commit"]:
        git(["fetch", "--quiet", "--depth", "1", "origin", target["commit"]], base)
    exclude = Path(git(["rev-parse", "--path-format=absolute", "--git-common-dir"], base)) / "info" / "exclude"
    exclude.parent.mkdir(parents=True, exist_ok=True)
    lines = exclude.read_text(encoding="utf-8").splitlines() if exclude.is_file() else []
    if ".codeatlas/" not in lines:
        exclude.write_text("\n".join(lines + [".codeatlas/"]) + "\n", encoding="utf-8")
    for mode in args.modes:
        root = worktree_path(bench_root, mode)
        if not (root / ".git").exists():
            print(f"Creating worktree {root}", flush=True)
            git(["worktree", "add", "--quiet", "--detach", str(root), target["commit"]], base)
        reset_worktree(root, target["commit"])
        for command in (["dotnet", "restore", target["solution"]],
                        ["dotnet", "build", target["solution"], "--no-restore", "-nologo", "-v:q"]):
            print(f"{mode}: {' '.join(command)}", flush=True)
            completed = subprocess.run(command, cwd=root, capture_output=True, text=True, errors="replace")
            if completed.returncode != 0:
                raise RuntimeError(f"{' '.join(command)} failed in {root}:\n{completed.stdout[-3000:]}{completed.stderr[-2000:]}")
    if "codeatlas" in args.modes:
        prepare_codeatlas(args, target, bench_root)  # bench_root is the suite's work root here
    print("Prepared. Validate with: run --dry-run", flush=True)


def create_local_repository(base, source, commit):
    """Commits a local suite's source with a fixed identity, date and LF line endings."""
    if not (base / ".git").exists():
        print(f"Creating local repository {base} from {source}", flush=True)
        base.mkdir(parents=True, exist_ok=True)
        for path in sorted(Path(source).rglob("*")):
            relative = path.relative_to(source)
            if path.is_dir() or {"bin", "obj", ".codeatlas"} & set(relative.parts):
                continue
            (base / relative).parent.mkdir(parents=True, exist_ok=True)
            (base / relative).write_bytes(path.read_bytes().replace(b"\r\n", b"\n"))
        git(["init", "--quiet"], base)
        for name, value in (("core.autocrlf", "false"), ("core.eol", "lf"), ("commit.gpgsign", "false")):
            git(["config", name, value], base)
        git(["add", "-A"], base)
        completed = subprocess.run(["git", "commit", "--quiet", "-m", "Benchmark source"], cwd=base, capture_output=True,
                                   text=True, env={**os.environ, **LOCAL_COMMIT_ENVIRONMENT})
        if completed.returncode != 0:
            raise RuntimeError(f"git commit failed in {base}: {completed.stderr.strip()}")
    head = git(["rev-parse", "HEAD"], base)
    if head != commit:
        raise RuntimeError(f"Local suite repository {base} is at {head}, the target pins {commit}. If the suite source "
                           "changed on purpose, update the commit in its target file and delete the suite folder.")


def prepare_codeatlas(args, target, bench_root):
    source = Path(args.codeatlas_bin).resolve()
    executable = source / ("codeatlas.exe" if os.name == "nt" else "codeatlas")
    if not executable.is_file():
        raise RuntimeError(f"CodeAtlas CLI not found at {executable}; build CodeAtlas first")
    tools = bench_root / "tools"
    copy = tools / "codeatlas-cli"
    if copy.exists():
        shutil.rmtree(copy)
    shutil.copytree(source, copy)
    root = worktree_path(bench_root, "codeatlas")
    environment = {name: os.environ[name] for name in WRAPPER_ENVIRONMENT if name in os.environ}
    environment.update(FIXED_WRAPPER_ENVIRONMENT)
    (tools / "codeatlas.json").write_text(json.dumps({"cli": str(copy / executable.name), "cwd": str(root),
                                                      "environment": environment}, indent=2), encoding="utf-8")
    shutil.copyfile(HERE / "codeatlas-wrapper.py", tools / "codeatlas.py")
    setup = {"wrapper": str(tools / "codeatlas.py"), "solution": str(root / target["solution"])}
    version = run_wrapper(setup, ["--version"], timeout=120)[2].strip()
    print(f"codeatlas: indexing {setup['solution']} with CodeAtlas {version}", flush=True)
    started = time.perf_counter()
    code, payload, _, stderr = run_wrapper(setup, ["index", "--solution", setup["solution"]])
    seconds = time.perf_counter() - started
    if code != 0 or not payload or not payload.get("success"):
        raise RuntimeError(f"CodeAtlas index failed ({code}): {stderr[-3000:]}")
    store = Path(payload["index"]).parent
    setup.update({
        "version": version, "manifest": payload["index"], "overview": payload["overview"], "indexId": payload["indexId"],
        "indexSeconds": round(seconds, 1), "reportedElapsedSeconds": payload.get("elapsedSeconds"),
        "projects": payload.get("projects"), "types": payload.get("types"), "methods": payload.get("methods"),
        "storeBytes": sum(path.stat().st_size for path in store.rglob("*") if path.is_file()),
        "modelCostUsd": 0, "createdAtUtc": payload.get("createdAtUtc")})
    (tools / "codeatlas-setup.json").write_text(json.dumps(setup, indent=2), encoding="utf-8")
    print(f"codeatlas: indexed in {seconds:.0f} s; setup saved to {tools / 'codeatlas-setup.json'}", flush=True)


# ---------------------------------------------------------------- dry run

def dry_run(args, target, tasks):
    problems, notes = [], []
    claude = shutil.which(args.claude)
    claude_version = tool_version([claude, "--version"])[0] if claude else None
    for name, present in (("git", shutil.which("git")), ("dotnet", shutil.which("dotnet")), (args.claude, claude)):
        if not present:
            problems.append(f"{name} not found on PATH")
    notes.append(f"claude: {claude_version or 'missing'} (only --version was run; no model call)")
    if args.provider == "ollama":
        try:
            ollama = ollama_status(args.ollama_url, args.model)
            notes.append(f"ollama: {ollama['version']} at {ollama['url']}, model {args.model}, num_ctx "
                         f"{ollama['numCtx'] or 'server default'} (no text generated)")
            if ollama["warning"]:
                problems.append("ollama: " + ollama["warning"])
        except RuntimeError as error:
            problems.append(str(error))
    lsp = lsp_status()
    notes.append("lsp: " + lsp["status"])
    setup = codeatlas_setup(args.work_root) if "codeatlas" in args.modes else None
    for mode in args.modes:
        root = worktree_path(args.work_root, mode)
        state = checkout_state(root, target["commit"])
        if mode == "lsp" and not lsp["available"]:
            notes.append(f"{mode}: worktree {state}; mode will be skipped")
            continue
        if state != "clean":
            problems.append(f"{mode}: worktree {root} is {state}; run prepare")
            continue
        if not (root / target["solution"]).is_file():
            problems.append(f"{mode}: {target['solution']} missing")
        notes.append(f"{mode}: worktree {root} clean at pinned commit")
    if "codeatlas" in args.modes:
        if not setup:
            problems.append("codeatlas: setup missing; run prepare --modes codeatlas")
        else:
            freshness = index_freshness(setup)
            if freshness["exitCode"] != 0 or freshness["isStale"] is not False:
                problems.append(f"codeatlas: index not usable through the wrapper: {freshness}")
            else:
                notes.append(f"codeatlas: {setup['version']}, index {setup['indexId']} {freshness['state']} through the wrapper; "
                             f"upfront index {setup['indexSeconds']} s, {setup['methods']} methods")
    pristine = worktree_path(args.work_root, next((m for m in args.modes if m != "lsp" or lsp["available"]), "plain"))
    for task in tasks:
        task_problems = []
        reference = task["reference"]
        if "answer" in reference:
            for check in task["checks"]:
                if check["type"] == "answer-set" and check["match"] == "path":
                    for relative in check["required"] + check.get("allowed", []) + check.get("forbidden", []):
                        if pristine.is_dir() and not (pristine / relative).is_file():
                            task_problems.append(f"expected file missing in checkout: {relative}")
            failed = [result for result in evaluate_checks(task, pristine, reference["answer"], run_commands=False)
                      if not result["passed"]]
            if failed:
                task_problems.append(f"reference answer fails its checks: {failed}")
            if all(result["passed"] for result in
                   evaluate_checks(task, pristine, {"files": [], "symbols": []}, run_commands=False)):
                task_problems.append("an empty answer passes")
        if pristine.is_dir():
            for check in task["checks"]:
                if check["type"] == "regex":
                    result = evaluate_regex(check, pristine)
                    if result["passed"] != check["pristine"]:
                        task_problems.append(f"regex {check['pattern']!r} on pristine checkout: passed={result['passed']}, "
                                             f"declared pristine={check['pristine']}")
        if args.verify_references and "patch" in reference and pristine.is_dir():
            task_problems += verify_reference_patch(task, pristine, target, SUITES[args.suite]["tasks"])
        (problems if task_problems else notes).append(f"task {task['id']}: " + ("; ".join(task_problems) or "valid"))
    modes = [mode for mode in args.modes if mode != "lsp" or lsp["available"]]
    for mode in modes:
        instruction = mode_instruction(mode, setup, args.compact_agent) if mode != "codeatlas" or setup else None
        for kind in (sorted({task["kind"] for task in tasks}) if args.compact_agent else [None]):
            command = claude_command(claude or args.claude, mode, args.model, session_budget(args), instruction,
                                     codeatlas_cli(setup) if mode == "codeatlas" and setup else None,
                                     args.unsafe_bypass_permissions, args.effort, args.claude_arg, args.max_turns, kind,
                                     worktree_path(args.work_root, mode))
            printable = [("<instruction: %d chars>" % len(part)) if instruction and part == instruction else
                         ("<system prompt: %d chars>" % len(part)) if kind and command[index - 1] == "--system-prompt"
                         else part for index, part in enumerate(command)]
            notes.append(f"{mode}{' ' + kind if kind else ''} command (prompt on stdin, cwd "
                         f"{worktree_path(args.work_root, mode)}): {printable}")
    calls = len(modes) * len(tasks) * args.repeat
    if args.provider == "ollama":
        notes.append(f"A real run would make {calls} local agent sessions ({len(modes)} modes x {len(tasks)} tasks x "
                     f"{args.repeat}) against Ollama; each is capped at {args.timeout} s"
                     + (f" and {args.max_turns} turns" if args.max_turns else "")
                     + "; no Anthropic key or Claude login is passed.")
    else:
        notes.append(f"A real run would make {calls} agent sessions ({len(modes)} modes x {len(tasks)} tasks x {args.repeat}); "
                     f"each is capped at ${args.max_budget_usd:g}, the run at ${args.max_total_usd:g}.")
    for note in notes:
        print("ok   " + note)
    for problem in problems:
        print("FAIL " + problem)
    print("Dry run " + ("passed" if not problems else f"found {len(problems)} problem(s)") + "; no model was called.")
    return 0 if not problems else 1


def verify_reference_patch(task, root, target, directory=HERE / "tasks"):
    problems = []
    patch = (Path(directory) / task["reference"]["patch"]).read_bytes().replace(b"\r\n", b"\n")
    reset_worktree(root, target["commit"])
    try:
        applied = subprocess.run(["git", "apply", "--whitespace=nowarn", "-"], cwd=root, input=patch, capture_output=True)
        if applied.returncode != 0:
            return [f"reference patch does not apply: {applied.stderr.decode(errors='replace').strip()}"]
        failed = [result for result in evaluate_checks(task, root, None) if not result["passed"]]
        if failed:
            problems.append(f"reference patch fails checks: {failed}")
    finally:
        reset_worktree(root, target["commit"])
    return problems


# ---------------------------------------------------------------- real run

def run_benchmark(args, target, tasks):
    claude = shutil.which(args.claude)
    if not claude:
        raise RuntimeError(f"{args.claude} not found on PATH")
    lsp = lsp_status()
    setup = codeatlas_setup(args.work_root) if "codeatlas" in args.modes else None
    if "codeatlas" in args.modes and not setup:
        raise RuntimeError("CodeAtlas setup missing; run prepare --modes codeatlas")
    ollama = None
    if args.provider == "ollama":
        ollama = ollama_status(args.ollama_url, args.model)
        if ollama["warning"]:
            raise RuntimeError("Ollama: " + ollama["warning"])
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    output = Path(args.results_dir).resolve() / f"{stamp}-{args.label}"
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "label": args.label, "startedAtUtc": stamp, "target": target, "model": args.model,
              "suite": args.suite, "compactAgent": args.compact_agent, "provider": args.provider, "ollama": ollama, "effort": args.effort, "repetitions": args.repeat,
              "maxBudgetUsdPerRun": session_budget(args), "timeoutSecondsPerRun": args.timeout, "maxTurns": args.max_turns,
              "claudeVersion": tool_version([claude, "--version"])[0], "platform": sys.platform,
              "isolation": "no setting sources plus agent-settings.json, empty strict MCP config, slash commands disabled, "
                           "auto memory off, allowlisted environment",
              "permissions": "bypassPermissions (unsafe opt-in)" if args.unsafe_bypass_permissions else "dontAsk with allowlist",
              "lsp": lsp, "codeatlas": {"version": setup.get("version"), "setup": setup} if setup else None,
              "tasks": [task["id"] for task in tasks], "runs": []}
    spent = 0.0
    for repetition in range(1, args.repeat + 1):
        for index, task in enumerate(tasks):
            # Rotate the mode order per task so no mode always runs first.
            offset = (index + repetition - 1) % len(args.modes)
            for mode in args.modes[offset:] + args.modes[:offset]:
                record = {"task": task["id"], "kind": task["kind"], "mode": mode, "repetition": repetition}
                if mode == "lsp" and not lsp["available"]:
                    record.update(status="skipped", reason=lsp["status"])
                elif spent >= args.max_total_usd:
                    record.update(status="skipped", reason=f"total budget ${args.max_total_usd:g} reached")
                else:
                    record.update(run_one(args, target, task, mode, repetition, claude, setup, output))
                    spent += record.get("costUsd") or 0
                report["runs"].append(record)
                report["summary"] = summarize(report["runs"])
                (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                print(f"{task['id']} / {mode} / r{repetition}: {record.get('status')} success={record.get('success')} "
                      f"tokens={record.get('totalTokens')} turns={record.get('turns')} cost={record.get('costUsd')}", flush=True)
    report["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    report["summary"] = summarize(report["runs"])
    (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    (output / "summary.md").write_text(render_markdown(report), encoding="utf-8")
    print(f"Results: {output}")
    return 0


def session_budget(args):
    return args.max_budget_usd if args.provider == "anthropic" else None


def ollama_config_dir(bench_root):
    path = Path(bench_root) / "tools" / "claude-home-ollama"
    path.mkdir(parents=True, exist_ok=True)
    return path.resolve()


def provider_cost(metrics, provider):
    """A local Ollama session costs nothing; Claude Code's own estimate is kept as reportedCostUsd."""
    record = dict(metrics, provider=provider)
    if provider == "ollama":
        record.update(costUsd=0.0, reportedCostUsd=metrics.get("costUsd"))
    return record


def run_one(args, target, task, mode, repetition, claude, setup, output):
    root = worktree_path(args.work_root, mode)
    directory = output / "runs" / task["id"] / f"{mode}-r{repetition}"
    directory.mkdir(parents=True)
    reset_worktree(root, target["commit"])
    record = {"status": "completed", "setupSeconds": 0.0}
    if mode == "codeatlas":
        freshness = index_freshness(setup)
        if freshness["isStale"] is not False:
            started = time.perf_counter()
            code, payload, _, stderr = run_wrapper(setup, ["refresh", "--solution", setup["solution"]])
            record["setupSeconds"] = round(time.perf_counter() - started, 1)
            record["refreshMode"] = ((payload or {}).get("refresh") or {}).get("mode")
            freshness = index_freshness(setup)
            if code != 0 or freshness["isStale"] is not False:
                return {"status": "skipped", "reason": f"CodeAtlas index could not be refreshed: {stderr[-300:]}"}
        record["indexFreshness"] = freshness["state"]
    instruction = mode_instruction(mode, setup, args.compact_agent)
    command = claude_command(claude, mode, args.model, session_budget(args), instruction,
                             codeatlas_cli(setup) if mode == "codeatlas" else None, args.unsafe_bypass_permissions,
                             args.effort, args.claude_arg, args.max_turns, task["kind"] if args.compact_agent else None,
                             root)
    prompt = build_prompt(target, task, args.compact_agent)
    config_dir = ollama_config_dir(args.bench_root) if args.provider == "ollama" else None
    (directory / "prompt.md").write_text(prompt + ("\n---\n" + instruction if instruction else ""), encoding="utf-8")
    started = time.perf_counter()
    timed_out = False
    with (directory / "stream.jsonl").open("w", encoding="utf-8") as stdout, \
            (directory / "stderr.log").open("w", encoding="utf-8") as stderr:
        process = subprocess.Popen(command, cwd=root, stdin=subprocess.PIPE, stdout=stdout, stderr=stderr,
                                   env=agent_environment(mode, provider=args.provider, ollama_url=args.ollama_url,
                                                         model=args.model, config_dir=config_dir),
                                   text=True, encoding="utf-8")
        try:
            process.communicate(prompt, timeout=args.timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
            else:
                process.kill()
            process.wait()
    record["elapsedSeconds"] = round(time.perf_counter() - started, 1)
    record["exitCode"] = process.returncode
    record["timedOut"] = timed_out
    metrics = parse_stream((directory / "stream.jsonl").read_text(encoding="utf-8", errors="replace").splitlines())
    record.update(provider_cost(metrics, args.provider))
    if mode == "lsp":
        record["lspToolLoaded"] = "LSP" in (metrics["initTools"] or [])
    raw_answer = root / ANSWER_FILE
    if task["answer"] and raw_answer.is_file():
        # Kept verbatim so a later rescore sees exactly what the agent wrote.
        shutil.copyfile(raw_answer, directory / "answer-raw.txt")
    answer, source, parse = extract_answer(root, metrics["finalText"]) if task["answer"] else (None, None, None)
    record.update(answerSource=source, answerParse=parse, answerLenient=is_lenient_parse(source, parse))
    if answer is not None:
        (directory / "answer.json").write_text(json.dumps(answer, indent=2), encoding="utf-8")
    diff = git(["diff"], root, check=False)
    if diff:
        (directory / "diff.patch").write_text(diff + "\n", encoding="utf-8")
    record["changedFiles"] = [line for line in git(["status", "--porcelain"], root, check=False).splitlines()][:50]
    checks = evaluate_checks(task, root, answer, directory)
    (directory / "checks.json").write_text(json.dumps(checks, indent=2), encoding="utf-8")
    record["checks"] = checks
    record["success"] = bool(checks) and all(check["passed"] for check in checks) and not timed_out
    reset_worktree(root, target["commit"])
    return record


# ---------------------------------------------------------------- rescore

def rescore_run(run, task, run_directory, root):
    """Re-evaluates a stored find/impact run with the current answer parser. Never calls a model."""
    raw = Path(run_directory) / "answer-raw.txt"
    stream = Path(run_directory) / "stream.jsonl"
    lines = stream.read_text(encoding="utf-8", errors="replace").splitlines() if stream.is_file() else []
    if raw.is_file():
        file_text, origin = raw.read_text(encoding="utf-8-sig", errors="replace"), "answer-raw.txt"
    else:
        file_text, origin = answer_file_from_stream(lines, run["mode"]), "stream"
    final_text = run.get("finalText")
    if final_text is None:
        final_text = parse_stream(lines)["finalText"]
    answer, source, parse = extract_answer(None, final_text, file_text)
    stored = run.get("checks") or []
    checks = []
    for index, check in enumerate(task["checks"]):
        if check["type"].startswith("answer-"):
            checks.append({"type": check["type"], "name": check["field"], **evaluate_answer(check, answer, root)})
        elif index < len(stored):
            checks.append(stored[index])
    success = bool(checks) and all(check["passed"] for check in checks) and not run.get("timedOut")
    return {"answerFileFrom": origin if file_text is not None else None, "answerSource": source, "answerParse": parse,
            "answerLenient": is_lenient_parse(source, parse), "answer": answer, "checks": checks, "success": success}


def rescore_report(directory, bench_root):
    """Writes rescore.json and rescore.md next to a stored results.json; results.json itself is left unchanged."""
    directory = Path(directory)
    report = json.loads((directory / "results.json").read_text(encoding="utf-8"))
    suite = report.get("suite") or "jellyfin"
    tasks = {task["id"]: task for task in load_tasks(SUITES[suite]["tasks"])}
    work_root = suite_root(bench_root, suite)
    runs, rows = [], []
    for run in report["runs"]:
        task = tasks.get(run["task"])
        rescored = dict(run)
        if run.get("status") == "completed" and task and task["answer"]:
            result = rescore_run(run, task, directory / "runs" / run["task"] / f"{run['mode']}-r{run['repetition']}",
                                 worktree_path(work_root, run["mode"]))
            rescored.update({key: value for key, value in result.items() if key != "answer"})
            rows.append({"task": run["task"], "mode": run["mode"], "repetition": run["repetition"],
                         "oldSuccess": run.get("success"), **result})
        runs.append(rescored)
    old, new = summarize(report["runs"]), summarize(runs)
    totals = {mode: {"runs": values["runs"], "oldSuccesses": old["modes"][mode]["successes"],
                     "newSuccesses": values["successes"], "lenientSuccesses": values["lenientSuccesses"]}
              for mode, values in new["modes"].items()}
    output = {"label": report.get("label"), "suite": suite, "results": str(directory), "totals": totals, "runs": rows}
    (directory / "rescore.json").write_text(json.dumps(output, indent=2), encoding="utf-8")
    lines = [f"# Rescore: {report.get('label')}", "",
             "Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.", "",
             "| Mode | Runs | Old success | New success | New via lenient parse |", "| --- | ---: | ---: | ---: | ---: |"]
    lines += [f"| {mode} | {values['runs']} | {values['oldSuccesses']} | {values['newSuccesses']} | {values['lenientSuccesses']} |"
              for mode, values in totals.items()]
    lines += ["", "| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |", "| --- | --- | ---: | --- | --- | --- | --- | --- |"]
    for row in rows:
        failed = "; ".join(f"{check['name']}: " + json.dumps({key: value for key, value in check.items()
                                                                if key in ("detail", "missing", "missingGroups", "forbidden")})
                           for check in row["checks"] if not check["passed"])
        lines.append(f"| {row['task']} | {row['mode']} | {row['repetition']} | {row['oldSuccess']} | {row['success']} | "
                     f"{row['answerSource'] or '-'} | {row['answerParse'] or '-'} | {failed or '-'} |")
    (directory / "rescore.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return output


def rescore(args):
    directories = args.results or sorted(path for path in Path(args.results_dir).iterdir() if (path / "results.json").is_file())
    for directory in directories:
        output = rescore_report(directory, Path(args.bench_root).resolve())
        print(f"{Path(directory).name} ({output['suite']}): " + "; ".join(
            f"{mode} {values['oldSuccesses']}/{values['runs']} -> {values['newSuccesses']}/{values['runs']} "
            f"(lenient {values['lenientSuccesses']})" for mode, values in output["totals"].items()), flush=True)
    return 0


# ---------------------------------------------------------------- entry point

def split_list(value, allowed=None):
    items = [item.strip() for item in value.split(",") if item.strip()]
    if allowed:
        unknown = [item for item in items if item not in allowed]
        if unknown:
            raise argparse.ArgumentTypeError("unknown: " + ", ".join(unknown))
    return items


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["prepare", "run", "rescore"])
    parser.add_argument("--results", type=Path, action="append", default=[],
                        help="rescore: a stored results directory (repeatable; default every one in --results-dir)")
    parser.add_argument("--bench-root", type=Path, default=DEFAULT_BENCH_ROOT,
                        help="Benchmark data outside the CodeAtlas source tree (default %(default)s)")
    parser.add_argument("--suite", choices=sorted(SUITES), default="jellyfin",
                        help="Task suite: jellyfin (pinned public target) or small (local 2-project solution)")
    parser.add_argument("--target", type=Path, default=None, help="Target file (default: the suite's)")
    parser.add_argument("--compact-agent", action="store_true",
                        help="Short replacement system prompt, short task prompt and the minimal tool set per task "
                             "(for small local models); permissions and isolation are unchanged")
    parser.add_argument("--modes", type=lambda value: split_list(value, MODES), default=list(MODES))
    parser.add_argument("--tasks", type=split_list, default=None, help="Comma-separated task ids (default: all)")
    parser.add_argument("--codeatlas-bin", type=Path,
                        default=HERE.parents[1] / "src" / "CodeAtlas.Cli" / "bin" / "Debug" / "net10.0",
                        help="Built CodeAtlas CLI directory copied by prepare")
    parser.add_argument("--claude", default="claude")
    parser.add_argument("--provider", choices=PROVIDERS, default="anthropic",
                        help="anthropic (billed, needs --confirm-paid-run) or ollama (local, no Anthropic key passed)")
    parser.add_argument("--ollama-url", default=DEFAULT_OLLAMA_URL, help="Ollama server for --provider ollama")
    parser.add_argument("--model", default=None,
                        help="Model alias or full name; recorded in results (default sonnet; required for ollama, "
                             "e.g. qwen3-8b-32k)")
    parser.add_argument("--effort", default=None, choices=["low", "medium", "high", "xhigh", "max"])
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--max-budget-usd", type=float, default=3.0, help="Per-session cap passed to claude")
    parser.add_argument("--max-total-usd", type=float, default=40.0, help="Stop starting sessions after this spend")
    parser.add_argument("--timeout", type=int, default=1800, help="Seconds per agent session")
    parser.add_argument("--max-turns", type=int, default=None, help="Optional turn cap passed to claude")
    parser.add_argument("--unsafe-bypass-permissions", action="store_true",
                        help="Run agents with bypassPermissions instead of dontAsk plus an allowlist. "
                             "Only inside a disposable container or VM.")
    parser.add_argument("--claude-arg", action="append", default=[], help="Extra argument for claude (repeatable)")
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--results-dir", type=Path, default=HERE / "results")
    parser.add_argument("--dry-run", action="store_true", help="Validate everything without calling a model")
    parser.add_argument("--verify-references", action="store_true",
                        help="With --dry-run: apply each reference patch and run all its checks, including builds")
    parser.add_argument("--confirm-paid-run", action="store_true",
                        help="Required for a real run: agent sessions are billed model calls")
    args = parser.parse_args(argv)
    if args.command == "rescore":
        return rescore(args)
    if args.repeat < 1 or args.max_budget_usd <= 0 or args.max_total_usd <= 0 or args.timeout <= 0 \
            or (args.max_turns is not None and args.max_turns < 1):
        parser.error("--repeat, --max-budget-usd, --max-total-usd, --timeout and --max-turns must be positive")
    if args.model is None:
        if args.provider == "ollama":
            parser.error("--model is required with --provider ollama (an installed Ollama model, e.g. qwen3-8b-32k)")
        args.model = "sonnet"
    if args.provider == "ollama" and not re.fullmatch(r"https?://[^\s/]+(/[^\s]*)?", args.ollama_url):
        parser.error("--ollama-url must be an http(s) URL")
    if not args.label or not re.fullmatch(r"[A-Za-z0-9_-]+", args.label):
        parser.error("Label must contain only letters, digits, hyphens and underscores")
    try:
        target = load_target(args.target or SUITES[args.suite]["target"])
        tasks = load_tasks(SUITES[args.suite]["tasks"], args.tasks)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    bench_root = Path(args.bench_root).resolve()
    if HERE.parents[2] in bench_root.parents or bench_root == HERE.parents[2]:
        parser.error("--bench-root must be outside the CodeAtlas source tree")
    args.work_root = suite_root(bench_root, args.suite)
    if args.command == "prepare":
        prepare(args, target)
        return 0
    if args.unsafe_bypass_permissions:
        print("WARNING: --unsafe-bypass-permissions lets agents run any command with your user rights. "
              "Use it only inside a disposable container or VM.", file=sys.stderr)
    if args.dry_run:
        return dry_run(args, target, tasks)
    if args.provider == "anthropic" and not args.confirm_paid_run:
        sessions = len(args.modes) * len(tasks) * args.repeat
        print(f"Refusing to start {sessions} paid agent sessions without --confirm-paid-run. Use --dry-run to validate.")
        return 2
    return run_benchmark(args, target, tasks)


if __name__ == "__main__":
    sys.exit(main())
