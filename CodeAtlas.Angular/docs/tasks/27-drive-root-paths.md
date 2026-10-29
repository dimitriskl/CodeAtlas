# 27 — Drive-root and filesystem-root workspaces

## What to build

Indexing a workspace located at a drive root (`C:\`) or the filesystem root (`/`) produces a valid storage folder name and valid workspace-relative file paths, or is refused with a clear documented error if that is the agreed behavior.

Source: QA finding on task 02.

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: ready

## Acceptance criteria

- [ ] The workspace name used in the storage folder is never empty; the rule for root paths is documented in `MCP-CONTRACT.md`.
- [ ] Stored file paths for a root workspace are workspace-relative with forward slashes and no leading slash or drive letter.
- [ ] Tests cover a Windows drive root and a POSIX root, using path inputs rather than real root folders.

## Review log
