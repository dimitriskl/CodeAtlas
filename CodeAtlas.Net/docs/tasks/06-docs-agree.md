# 06 — Docs agree with the release

## What to build

Make the user-facing docs describe what ships.

- Remove "no MCP layer" or "no MCP setup" from README (intro and history note), USER-GUIDE and ROADMAP.
- Add the MCP tools and the release decision to EXECUTION: a GitHub release now, NuGet.org later, and the benchmark gate.
- The README quick start covers:
  - installing from the release `.nupkg`;
  - indexing a solution;
  - registering the MCP server.
  Examples use `<project_name>`.
- State where the index, reports and logs are written.

Keep it concise and command-oriented, with no unfinished-status commentary. Leave VERSION.md history entries as they are.

User stories: 16, 17, 18, 19. PRD: [Release (V1)](../prd/0001-net-release.md#release-v1).

## Blocked by

- [04 — search_symbols](04-search-symbols.md)
- [05 — find_references](05-find-references.md)
- [17 — Malformed request lines](17-mcp-protocol-surrogates.md) (merge first so the docs describe the fixed server)

Status: done (https://github.com/dimitriskl/CodeAtlas/pull/18; merged as 13afb0d)

## Acceptance criteria

- [x] No user-facing doc says there is no MCP layer or no MCP setup. Existing VERSION.md history is unchanged.
- [x] README quick start has working commands for release install, index and MCP config, using `<project_name>`.
- [x] README and USER-GUIDE list all six MCP tools and link the contract.
- [x] Output locations (index, current manifest, reports, logs) are stated once and correctly.
- [x] EXECUTION records the GitHub release as V1 and NuGet.org as deferred.
- [x] No private project or customer names appear in any doc.
- [x] The quick start commands are run on a clean machine or container, and the transcript is in the PR.

## Review log

### 2026-11-04 (implementation and local preflight)

- README now leads with release-package installation, solution indexing and a stdio MCP configuration using `<project_name>`. README and USER-GUIDE list all six tools and link QUERY-CONTRACT. Tool calls explicitly use the absolute solution directory as `workspace`.
- USAGE has the shared output-location table, including stderr redirection, live warnings and client-captured MCP logs. README, USER-GUIDE and EXECUTION link it. ROADMAP and EXECUTION follow PRD 0001: GitHub release first, NuGet.org deferred, the measured benchmark win rule, and separate release/paid-run approvals.
- Removed contradictory current MCP statements without rewriting VERSION.md history; the required `0.00.67` entry is appended. Public examples were checked for private names and local Markdown targets were verified.
- Qualification now reads the MCP configuration from the README inside the `.nupkg`, substitutes only executable/solution paths and starts that configured process. The installed smoke keeps `quickstart-mcp.json` and `quickstart-transcript.txt` alongside full command stdout/stderr, automatically uploaded by the existing no-checkout install jobs. It validates the stdio launch and tool exchange, not a desktop client's settings UI.
- Before the documentation edit, checking for the executable MCP example failed because README had no configuration. Afterward the configuration parses and the real installed-tool smoke passes.
- Local Windows Release preflight at `0.00.67`: build zero warnings/errors; isolated package installation, help/version, restore, index/status, MCP initialize/list, `search_symbols`, `find_references`, stale refusal and reindex all pass. Public fixture: 2 projects, 2 types, 6 methods. Package SHA-256 `ab5bfa44ce6e7f5f66fda3c8d75dc4d8dbd4cd73e3f11723fbab69db884f2f78`. Transcript: `.codeatlas/task06-20261104/installed-tool-smoke/quickstart-transcript.txt`.
- At preflight, full local checks, independent review and hosted Windows/Linux qualification remained open; the completion entry below records their results. A local isolated install alone is not clean-machine evidence.

### 2026-11-04 (independent review)

- Standards review of `d8d1f69...7c17363`: zero documented violations or actionable baseline smells. Existing version history and generated-output exclusions are preserved.
- Spec review found that the new MCP table advertised a declaring-type filter implemented only by the CLI. Corrected README and USER-GUIDE to the MCP schema's exact/qualified name, kind and project filters. Also qualified the README's saved-index statement as "Indexed queries", preserving the distinction from live CLI analysis.

### 2026-11-04 (qualification and completion)

- Both Spec findings were rechecked at `55b13cd`: zero unresolved findings. Standards review had zero findings. No production source or application contract changed.
- Local Release validation at `0.00.67`: zero build warnings/errors, .NET 967/967 in 14.7 minutes, Python 145 passed. Four README PowerShell examples parse; links into the edited guide headings resolve; every earlier VERSION.md history entry is unchanged.
- Regenerated CodeAtlas's own index as `e431493082674b39be80f2f734a17c8d`; `index-status` reports `verified-generation`, `isStale: false`, with the documented partial-input-coverage limits.
- Final local installed-tool smoke passed all 17 steps. Final local package SHA-256: `89c47faadfdb4ec36b558fa7a3100732d0a6934f558314101e59e15072557de1`. Evidence stays under `.codeatlas/task06-20261104/`.
- [Qualification run 41776392898](https://github.com/dimitriskl/CodeAtlas/actions/runs/41776392898), at `55b13cd`: all four Windows/Linux build/install jobs passed. Ubuntu's CRAP gate passed against the existing exception baseline, which remains task 15. macOS is task 08's release/manual gate.
- Downloaded and independently checked both clean no-checkout install artifacts: `install-evidence-windows-2025` and `install-evidence-ubuntu-24.04`. Each passed all 17 steps, printed `0.00.67`, indexed 2 projects / 2 types / 6 methods, and launched exactly the resolved configuration from the packaged README. Actual MCP replies confirmed all six tools plus successful `search_symbols` and `find_references` calls.
- Both clean hosts installed the same qualified Windows-built package, SHA-256 `2e45b5a3976399c154218024553a113d04c0b6f101efccad8926d3dc6ccf7c7e`. The resolved configuration, transcript and full outputs are in the install artifacts; normalized hosted command/result transcripts and artifact links are included in PR #18. Install/restore use an isolated local NuGet configuration; the claim is qualified command execution and stdio exchange, not desktop settings-UI automation.
- Merged PR #18 as `13afb0d610e413722dbb6ab68007ac176d59f49f`. Task 06 is complete; task 07 is next. These completion records belong to the same `0.00.67` change.
