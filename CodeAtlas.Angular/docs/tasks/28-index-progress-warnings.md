# 28 — Indexing progress and warnings

## What to build

`index` reports progress while it runs and returns every skipped file with its reason in the envelope's warnings, so a developer knows what was skipped and why.

User stories: 35. PRD: CLI envelope (`warningCount`).

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: progress lines go to stderr only; stdout stays a single valid JSON envelope.
- [ ] A file that cannot be parsed, an excluded symlink, or an unsupported file is listed as a warning with its path and a reason code.
- [ ] `warningCount` equals the number of warnings listed.
- [ ] `index_status` reports the warning count of the current generation.
- [ ] Warning reason codes are documented in `MCP-CONTRACT.md`.

## Review log
