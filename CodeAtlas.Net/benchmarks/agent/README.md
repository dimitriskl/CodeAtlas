# Agent benchmark

Measures whether a coding agent finishes C# navigation and change tasks with fewer tokens and turns, and at least the same success, when it uses CodeAtlas. The same agent (headless Claude Code) and model run every task in three modes:

| Mode | Tools | Extra instruction |
| --- | --- | --- |
| `plain` | Bash, Read, Edit, Write, Glob, Grep | none |
| `lsp` | the same plus the Claude Code `LSP` tool (csharp-ls through `lsp-plugin/`) | [prompts/lsp.md](prompts/lsp.md) |
| `codeatlas` | the same as plain; CodeAtlas is called through Bash | [prompts/codeatlas.md](prompts/codeatlas.md) |

All modes share the same isolation: no setting sources (`--setting-sources ""`) plus the harness-owned [agent-settings.json](agent-settings.json) (hooks disabled, web tools denied), so the cloned repository's `.claude` settings, hooks and permissions are never loaded; an empty `--strict-mcp-config`; `--disable-slash-commands`; `--no-session-persistence`; auto memory and auto-update off; and a per-session `--max-budget-usd`. Permissions use `--permission-mode dontAsk` with an allowlist: Read, Grep, Glob, Edit, Write, `dotnet build`, `dotnet test`, `git diff`, `git status`, plus the CodeAtlas wrapper in codeatlas mode and LSP in lsp mode; everything else is denied without a prompt. The agent process receives an allowlisted environment (path, profile, temp and system variables, `DOTNET_*`, and `ANTHROPIC_API_KEY` when set); other provider keys and tokens are not passed. `--unsafe-bypass-permissions` switches to `bypassPermissions` and prints a warning; use it only inside a disposable container or VM.

Output is `--output-format stream-json --verbose`: its final `result` event is the same object as `--output-format json` (turns, cost, usage per model), and the earlier events show which tools were loaded and called.

## Target

[target.json](target.json) pins jellyfin/jellyfin `v10.11.9` (`e83a7e62f26443f7dd98f126d6955ac1af090125`): 40 projects, about 2,000 C# files, 6,527 indexed methods. It restores and builds with the .NET 9 SDK selected by its `global.json`. The checkout lives outside this repository (default `C:\temp\codeatlas-bench`); it is benchmark data, not development.

## Tasks

Each task in [tasks/](tasks/) has a prompt, objective checks and a reference solution that the dry run verifies.

| Task | Kind | Check |
| --- | --- | --- |
| `find-password-verification` | find | answer names `DefaultAuthenticationProvider.Authenticate` and its file |
| `find-episode-number-parsing` | find | answer names `EpisodePathParser.Parse` and its file |
| `impact-metadata-saver-implementations` | impact | all 8 concrete `IMetadataSaver` classes (most through base classes), precision at least 0.8 |
| `impact-static-media-sources` | impact | all 4 files that call or implement `IMediaSourceManager.GetStaticMediaSources`; none of the files that only use the same-named `IChannelManager` method |
| `change-changepassword-cancellation` | change | new `CancellationToken` on `IUserManager.ChangePassword` only (not `IAuthenticationProvider.ChangePassword`); solution builds, including analyzers that require the token to be forwarded |
| `change-saveuserdata-overload-rename` | change | only the `UserItemData` overload of `SaveUserData` renamed at every call site; DTO overload untouched; solution builds |

Find and impact tasks write `codeatlas-bench-answer.json` (`files`, `symbols`) in the repository root. Change tasks are checked by source patterns and `dotnet build Jellyfin.sln`.

### Answer parsing

The answer format is read tolerantly; what counts as a correct answer is not. Small models often write broken JSON, so a failed format check would measure JSON formatting instead of navigation.

- Accepted, in order: the answer file, then the agent's final message. In each, a JSON object (`files`/`file`/`paths`, `symbols`/`symbol`; a single string becomes a list), a bare list (entries with a slash or a source extension are files, the rest symbols), a double-encoded JSON string, JSON with escaped quotes but no outer quotes, literal `\n` between tokens, Windows paths with single backslashes, JSON in a markdown fence or embedded in prose. Only if neither contains JSON are file paths and symbol names extracted from the text (`text-fallback`).
- Paths: `\` and `/` are equal, case is ignored, `./` and absolute paths under the worktree are made repository-relative.
- Symbols: namespaces and containing types are optional where the check allows it (an `answer-set` by name compares the last segment; `answer-contains` still needs the pattern, e.g. `CouponValidator.IsExpired`, to occur). Parameter lists, generic arguments and arity (`` `1 ``), `+` for nested types, `::`, `global::` and CodeAtlas symbol IDs (`Project.csproj::M:Ns.Type.Member(...)`) are normalized.
- Unchanged: every required entry must be present, precision limits apply, and forbidden entries (such as the same-named method on an unrelated type) still fail the run.
- Each run records `answerSource` (`file`, `final-text`), `answerParse` (for example `json-object`, `double-encoded+json-object`, `escaped-quotes+backslashes+json-object`, `backslashes+json-list`, `text-fallback`) and `answerLenient` (the strict reader, a JSON object in the file or a fence, would not have read it). The raw answer file is kept as `answer-raw.txt`. `summary.md` counts parses and the successes that needed a lenient parse.

`rescore` re-evaluates stored find and impact runs with the current parser, without model calls. It reads `answer-raw.txt`, or rebuilds the answer file from the session's successful Write and Edit calls in `stream.jsonl`, and writes `rescore.json` and `rescore.md` next to `results.json` (left unchanged). Change tasks keep their stored result.

```powershell
python benchmarks/agent/agent-benchmark.py rescore                     # every directory under results/
python benchmarks/agent/agent-benchmark.py rescore --results benchmarks/agent/results/<stamp>-<label>
```

## Run

From `CodeAtlas.Net`, after building CodeAtlas:

```powershell
python benchmarks/agent/agent-benchmark.py prepare            # clone, worktree per mode, restore, build, CodeAtlas index
python benchmarks/agent/agent-benchmark.py run --dry-run --verify-references
python benchmarks/agent/agent-benchmark.py run --model sonnet --label baseline --confirm-paid-run
```

`prepare` and `--dry-run` never call a model. A real run starts `modes x tasks x --repeat` agent sessions (18 with the defaults) and refuses to start without `--confirm-paid-run`. Each session is capped by `--max-budget-usd` (default 3) and the run stops starting sessions after `--max-total-usd` (default 40). Use `--modes`, `--tasks` and `--repeat` to narrow or widen a run.

Every session starts and ends with `git reset --hard <commit>` and `git clean -fd` in that mode's worktree; build outputs and the CodeAtlas store stay because they are ignored. Before each CodeAtlas session the harness checks index freshness and refreshes it when needed; that time is reported as `setupSeconds`, separate from agent time. The upfront index time is recorded once by `prepare` in `<bench-root>/tools/codeatlas-setup.json`; it has no model cost.

Results go to `results/<UTC stamp>-<label>/`: `results.json` (every run with tokens, cost, turns, elapsed time, tool counts, checks, success) and `summary.md`, plus each session's prompt, event stream, answer, diff and check logs.

Total tokens are input + cache creation + cache read + output, summed over every model the session used. Comparisons with plain use the tasks that both modes completed.

## Local model (Ollama)

`--provider ollama` runs the same headless Claude Code against a local Ollama server, which serves the Anthropic Messages API. Nothing is billed and `--confirm-paid-run` is not needed.

- The agent gets `ANTHROPIC_BASE_URL` (`--ollama-url`, default `http://localhost:11434`) and the placeholder `ANTHROPIC_AUTH_TOKEN=ollama`. `ANTHROPIC_API_KEY` is never passed in this mode, and `CLAUDE_CONFIG_DIR` points at an empty harness-owned home (`<bench-root>/tools/claude-home-ollama`), so the user's `/login` credentials are not visible either (the init event reports `apiKeySource: none`, recorded per run). A misrouted request therefore cannot be billed. Every Claude Code model alias (main, background, subagent) points at the local model, and non-essential traffic is disabled. `--max-budget-usd` is not passed, because Claude Code prices unknown models as if they were billed.
- `--model` is required and must be an installed Ollama model. Before any session the harness checks `/api/version`, `/api/tags` and `/api/show` (no text generation) and refuses to run when the model is missing or its `num_ctx` is below 16,384. The harness never pulls models.
- Context length: Ollama loads models with a 4,096-token context by default (`ollama ps`), which truncates Claude Code's system prompt and tool definitions. Create a derived model with a larger context; this reuses the existing weights and downloads nothing:

  ```powershell
  "FROM qwen3:8b`nPARAMETER num_ctx 32768" | Set-Content Modelfile
  ollama create qwen3-8b-32k -f Modelfile
  ```

  At 32,768 tokens qwen3:8b (Q4_K_M) uses about 9.8 GB and runs fully on a 12 GB GPU.
- Results record `provider: "ollama"`, the Ollama version, URL and `num_ctx`, and `costUsd: 0`; Claude Code's own estimate is kept as `reportedCostUsd`. The time cap (`--timeout`) and an optional `--max-turns` apply per session. When a session times out without a final result event, tokens and turns are summed from its assistant messages (`usageSource: "assistant-messages"`).

```powershell
python benchmarks/agent/agent-benchmark.py run --dry-run --provider ollama --model qwen3-8b-32k --modes plain,codeatlas
python benchmarks/agent/agent-benchmark.py run --provider ollama --model qwen3-8b-32k --modes plain,codeatlas --timeout 1200 --max-turns 40 --label ollama-qwen3-8b
```

A small local model measures the harness and the tools' usability for weak agents; its numbers are not comparable with a Claude baseline.

## Small suite and compact agent

Jellyfin is too large for an 8B model with a 32k context. `--suite small` selects a second suite; the default `--suite jellyfin` is unchanged.

- Target: [target-small.json](target-small.json) and the source in [small/Shop](small/Shop): two projects (`Shop.Core`, `Shop.App`), 23 C# files. `IPaymentProcessor` has two direct and two base-class implementations plus a look-alike `FakeGatewayClient`; `DiscountCalculator.Apply` has two overloads; `IOrderRepository.GetOpenOrders` has a same-named method on the unrelated `IReportService`.
- `prepare --suite small` commits the source into `<bench-root>/suites/small/shop` with a fixed author, date and LF endings, so the pinned commit is reproducible, then creates worktrees, builds and indexes it like Jellyfin. If the source changes on purpose, update the commit in the target file and delete that folder.
- Tasks in [tasks-small/](tasks-small/), checked like the Jellyfin tasks:

| Task | Kind | Check |
| --- | --- | --- |
| `find-coupon-expiry` | find | `CouponValidator.IsExpired` and its file |
| `impact-payment-processors` | impact | the 4 concrete `IPaymentProcessor` classes (2 through a base class), not `FakeGatewayClient`, precision at least 0.8 |
| `impact-open-orders-callers` | impact | the 3 files that call or implement `IOrderRepository.GetOpenOrders`; none that only use `IReportService.GetOpenOrders` |
| `change-coupon-overload-rename` | change | only the `Coupon` overload renamed to `ApplyCoupon`, its callers updated, the `decimal` overload and its caller untouched; builds |
| `change-charge-currency` | change | `string currency` added to `IPaymentProcessor.Charge` and all implementations, `"EUR"` passed by the caller, `FakeGatewayClient` untouched; builds |

`--compact-agent` shrinks the agent's context for small models while keeping the same permissions and isolation (dontAsk allowlist, no setting sources, empty MCP config, environment allowlist):

- `--system-prompt` replaces Claude Code's default system prompt with [prompts/system-compact.md](prompts/system-compact.md) (working directory, path rules, shell limits) plus the short mode instruction ([prompts/codeatlas-compact.md](prompts/codeatlas-compact.md) in codeatlas mode).
- A shorter task prompt ([prompts/task-compact.md](prompts/task-compact.md)).
- The minimal tool set per task: find and impact get Read, Grep, Glob and Write; change tasks get Read, Grep, Glob, Edit and Bash (for `dotnet build`); codeatlas mode always adds Bash for the CLI. Plain therefore lacks Bash on find and impact tasks; codeatlas mode has it only to run CodeAtlas.

```powershell
python benchmarks/agent/agent-benchmark.py prepare --suite small --modes plain,codeatlas
python benchmarks/agent/agent-benchmark.py run --dry-run --verify-references --suite small --compact-agent --provider ollama --model qwen3-8b-32k --modes plain,codeatlas
python benchmarks/agent/agent-benchmark.py run --suite small --compact-agent --provider ollama --model qwen3-8b-32k --modes plain,codeatlas --repeat 3 --timeout 900 --max-turns 30 --label ollama-qwen3-8b-small-compact
```

## Known limits

- **Run real benchmarks in a disposable container or VM.** The allowlist and environment filtering reduce risk, but the agent still edits and builds a third-party repository with your user rights; full isolation is recommended for paid runs.
- **LSP mode needs csharp-ls.** It is not installed on the development machine, so `run` records the LSP mode as skipped. Install it with `dotnet tool install --global csharp-ls`, then rerun the dry run. Whether Claude Code loads the plugin from `--plugin-dir` with no setting sources, and whether `dontAsk` permits csharp-ls, can only be confirmed by a real session: each LSP run records `lspToolLoaded` from the session's init event, and a run without it is not an LSP measurement.
- Commands outside the allowlist (pipes such as `| head`, `findstr`, `rg`) are denied; agents search with the Grep and Glob tools in every mode.
- **CodeAtlas runs through a wrapper.** `prepare` copies [codeatlas-wrapper.py](codeatlas-wrapper.py) to `<bench-root>/tools/codeatlas.py`, which runs a copied CLI with a fixed environment and no provider credentials. A current index does not go stale just because the agent shell has a different working directory: freshness hashes only environment variables the solution's own project files reference. The wrapper still keeps the benchmark process free of keys.
- Agent instructions in user-level files may still apply if Claude Code loads them with no setting sources; this is the same for every mode.
- One repetition is noisy. Use `--repeat 3` for decisions.
