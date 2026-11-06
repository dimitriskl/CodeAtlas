# 08 — Demo and tagged GitHub release

## What to build

- A short demo, as a script or a recording, of an agent installing from the release, indexing `<project_name>` and answering a question through the MCP server.
- Bump the version by the docs/VERSION.md rules, in its own commit.
- Create a tagged GitHub release with the `CodeAtlas.Tool` `.nupkg` built by qualification. Release notes list the MCP tools and the install command.

NuGet.org is not part of this task. Creating the tag and the release is an external action: it needs Dimitris's explicit approval at the time.

User stories: 16, 20. PRD: [Release (V1)](../prd/0001-net-release.md#release-v1).

## Blocked by

- [06 — Docs agree](06-docs-agree.md)

Status: done (PR #24; release v0.0.73 published on 2026-11-05)

## Acceptance criteria

- [x] The version is bumped with the bump script on a clean clone. README and VERSION.md show the same version.
- [x] The release `.nupkg` is the qualified package. Its version matches the tag (NuGet form, for example `0.0.57` for `0.00.57`).
- [x] Installing from the published release asset and running `codeatlas --version` and the MCP handshake works on Windows, Linux and macOS. The `v*` tag run or a manual run gives the macOS evidence, because pull requests do not run macOS.
- [x] The demo is linked from README and uses `<project_name>`.
- [x] Tag and release are created only after Dimitris's explicit yes.

## Review log

### 2026-11-04 (preparation)

- [Demo guide](../DEMO.md) reuses the qualified standalone installed-package client. It installs into an isolated tool directory, indexes the public fixture, initializes MCP, searches a symbol and answers where the integer `Calculate` overload is invoked. It is explicitly a scripted MCP client, not an unmeasured model-quality demonstration.
- [Release notes](../releases/v0.0.73.md) list all six tools, the framework-dependent .NET 10 requirement and the install command. README links the demo. Current execution guidance records the owner's local-Ollama-only decision.
- Planned tag: `v0.0.73`, matching NuGet package `0.0.73` and product `0.00.73`. Planned assets: the qualified tool nupkg, `package-fixture.zip`, standalone `package-smoke.py` client and a SHA-256 manifest. Asset bytes and source commit will be pinned after qualification.
- At the end of preparation, owner approval, tag/release creation and downloaded-asset verification remained. They were completed on 2026-11-05, as recorded below.

- Clean-clone probe at source `5841f84` began with an empty Git status and generated `0.00.73` using `bump-version.py`; only Version.props, README and VERSION changed. Those exact verified outputs form the separate version commit. Local qualification was then running; publication followed on 2026-11-05.

### 2026-11-04 (qualification complete)

- [PR #24](https://github.com/dimitriskl/CodeAtlas/pull/24) merged as `3c576791c09b9824b2a033bbbf45dd935793d3c8`; qualified source remains `55133fcd73f29f0f961894dbfdd5a8321fbc44e4` for tag `v0.0.73`.
- Local Release build has zero warnings/errors; 1,095 .NET and 155 Python tests, payload inspection and isolated installed-package smoke passed. Own index: `59370983ae4e4a2e8a81f0923da36cf0` at `0.00.73`.
- [All six hosted jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41890052777) passed. Windows/Linux/macOS full suites each passed 1,095 .NET and 155 Python cases. Ubuntu CRAP gate has zero violations, exception problems or improvement warnings. The first macOS install could not acquire a hosted runner and executed no steps; only that job was retried, and it passed.
- All three checkout-free install hosts used the identical Windows-built package: SHA-256 `9ea031df79dfa7b04dd87188b08ad2be822f524e5095829f29091818ff9b00a9`; each passed all 17 smoke stages, including version, indexing, MCP handshake and stale refusal. A local demo using those exact bytes also passed.
- Standards and Spec reviews have zero blocking findings. The historical preparation-log remaining list is corrected here. Release notes, package, public fixture ZIP, standalone client and SHA256SUMS are prepared. Publication approval was requested explicitly and arrived on 2026-11-05; publication details follow.

### 2026-11-05 (publication complete)

- After the explicit publication request, the owner instructed “go on”; [release `v0.0.73`](https://github.com/dimitriskl/CodeAtlas/releases/tag/v0.0.73) was published at qualified commit `55133fcd73f29f0f961894dbfdd5a8321fbc44e4`. NuGet.org was not used.
- Downloaded all four published assets and verified they are byte-identical to the reviewed qualified inputs, including the package hash above. The tag resolves to the qualified commit. The standalone client downloaded from the release installed those bytes and passed all 17 smoke stages locally. Windows/Linux/macOS install evidence already exercises the identical package bytes.
- Task 08 is complete. The separate task-09 experiment finished all 60 sessions with zero billed cost; its published-rule verdict is fail, not a release-package defect.
