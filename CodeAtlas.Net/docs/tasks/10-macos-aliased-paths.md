# 10 — macOS solutions reached through a symlinked path

## What to build

Make a solution reached through a symlinked path on macOS either work or fail with one clear error.

On macOS, `/var/folders` and `/tmp` are aliases of `/private/var/folders` and `/private/tmp`. When the solution path has a symlinked component, CodeAtlas mixes the alias and the real path. In run 41005467202 (task 01, without the `TMPDIR` step) this caused:

- live host queries failed (`SqliteFileUpdateTests`);
- MCP publish hit `DirectoryNotFoundException` on `.write.lock` (`McpIndexCommandTests`);
- publish reported `stale_index` right after a build (`SplitCatalogStorageTests`);
- `describe` refused with "Reparse/alias context paths are not supported" (`ExplanationWorkflowTests`, `ExplanationCliContractTests`).

Symlink aliases are documented as outside the qualified path model (README, SNAPSHOT-RELIABILITY.md), but users get misleading stale or I/O errors, not a clear refusal.

Choose one:

- Resolve the solution path to its real path at the CLI, live and MCP entry points; or
- Refuse an aliased solution path with one documented error code.

Then remove the macOS `TMPDIR` step from `.github/workflows/qualification.yml` and `build/qualification.yml`.

User stories: 21. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: deferred until after the release

## Acceptance criteria

- [ ] A test indexes `tests/Fixtures/Sample.slnx` through a symlinked folder on Linux and macOS and asserts the chosen behavior: queries, live, MCP publish and freshness work, or every entry point returns the one documented error.
- [ ] If a new error code is added, it is written in `docs/QUERY-CONTRACT.md` before the implementation changes.
- [ ] README and SNAPSHOT-RELIABILITY.md state the behavior for symlinked paths.
- [ ] The macOS `TMPDIR` workflow step is removed, and both workflow copies stay identical.
- [ ] Qualification is green on Windows, Linux and macOS.
- [ ] No change to CLI output, exit codes or schema for non-aliased paths.

## Review log
