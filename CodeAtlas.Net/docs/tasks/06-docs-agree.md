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

Status: ready

## Acceptance criteria

- [ ] No user-facing doc says there is no MCP layer or no MCP setup. VERSION.md history is unchanged.
- [ ] README quick start has working commands for release install, index and MCP config, using `<project_name>`.
- [ ] README and USER-GUIDE list all six MCP tools and link the contract.
- [ ] Output locations (index, current manifest, reports, logs) are stated once and correctly.
- [ ] EXECUTION records the GitHub release as V1 and NuGet.org as deferred.
- [ ] No private project or customer names appear in any doc.
- [ ] The quick start commands are run on a clean machine or container, and the transcript is in the PR.

## Review log
