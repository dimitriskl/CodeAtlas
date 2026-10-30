# 08 — Demo and tagged GitHub release

## What to build

- A short demo, as a script or a recording, of an agent installing from the release, indexing `<project_name>` and answering a question through the MCP server.
- Bump the version by the docs/VERSION.md rules, in its own commit.
- Create a tagged GitHub release with the `CodeAtlas.Tool` `.nupkg` built by qualification. Release notes list the MCP tools and the install command.

NuGet.org is not part of this task. Creating the tag and the release is an external action: it needs Dimitris's explicit approval at the time.

User stories: 16, 20. PRD: [Release (V1)](../prd/0001-net-release.md#release-v1).

## Blocked by

- [06 — Docs agree](06-docs-agree.md)

Status: ready

## Acceptance criteria

- [ ] The version is bumped with the bump script on a clean clone. README and VERSION.md show the same version.
- [ ] The release `.nupkg` is the qualified package. Its version matches the tag (NuGet form, for example `0.0.57` for `0.00.57`).
- [ ] Installing from the release asset and running `codeatlas --version` and the MCP handshake works on Windows, Linux and macOS.
- [ ] The demo is linked from README and uses `<project_name>`.
- [ ] Tag and release are created only after Dimitris's explicit yes.

## Review log
