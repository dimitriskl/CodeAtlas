# 25 — Symlink dedup

## What to build

The workspace loader resolves every included source file to its real path and indexes each real file once. A symlink that points at a file already included never creates a second file or symbol. A duplicate-id failure, if it still happens, is reported as a clean documented error without raw SQLite text.

User stories: 2, 35. Source: Tester and QA findings on task 02.

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: a fixture with a symlink to an included file indexes successfully, and the file and its symbols appear exactly once.
- [ ] The stored path is the real file's workspace-relative path, not the symlink's.
- [ ] A symlink to a file outside the workspace is skipped and reported as a warning.
- [ ] No user-facing error contains raw SQLite error text; storage failures map to a documented error code.
- [ ] The real-path rule is written in `MCP-CONTRACT.md` under the storage naming rules, so the .NET edition can match it.

## Review log
