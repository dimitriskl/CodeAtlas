# 02 — Standards checks in the build

## What to build

Turn the coding standards from practice into checks.

- Add `.editorconfig` with the C# style rules the code already follows, and enforce code style in the build.
- Set warnings as errors for every project.
- Add a CRAP (complexity × coverage) gate with limit 10 to CI. Collect coverage from the existing test run.
- Fix or justify each existing violation. A justified exception is listed in one place with a reason.
- Update CODING_STANDARDS.md so these items move from "practice" to "checked", and name the command that checks each.

User stories: 22, 23. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: ready

## Acceptance criteria

- [ ] `.editorconfig` exists, and code style is enforced in the build.
- [ ] Warnings are errors in every project. A clean build has zero warnings.
- [ ] CI fails when any method has a CRAP score above 10, unless it is a listed exception with a reason.
- [ ] A test or CI proof shows each gate fails on a deliberate violation.
- [ ] CODING_STANDARDS.md lists each rule as checked, with its command.
- [ ] Qualification is green on Windows, Linux and macOS.
- [ ] No change to CLI output, exit codes, MCP output or schema.

## Review log
