# 26 — Storage-naming test oracle

## What to build

The storage-folder naming tests check fixed expected values taken from the documented naming rules (worked examples in `MCP-CONTRACT.md`), instead of recomputing the hash the way the code does.

Source: QA finding on task 02.

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: ready

## Acceptance criteria

- [ ] `MCP-CONTRACT.md` lists at least three worked examples of workspace path to folder name, including a Windows path and a POSIX path.
- [ ] Tests assert those literal folder names and do not call the production hashing code to build the expected value.
- [ ] Tests pass on Windows and Ubuntu.

## Review log
