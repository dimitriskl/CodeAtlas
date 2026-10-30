# 24 — RealWorld benchmark run and report

## What to build

Run 6 frozen tasks on the Angular RealWorld example app (github.com/gothinkster/angular-realworld-example-app), 3 reps per mode, using the released package.

Write a report that compares the results with the v1 goal (equal or better success, ≥20% fewer tokens) and notes the .NET edition's +29% tokens result on an 8B model.

User stories: 50, 51, 52. PRD: [Testing Decisions](../prd/0001-initial.md#testing-decisions).

## Blocked by

- [22 — README and release packaging](22-readme-release-packaging.md)
- [23 — Benchmark fixture app and harness](23-benchmark-fixture-harness.md)

Status: ready

## Acceptance criteria

- [ ] 6 tasks with reference answers frozen before measuring, and the RealWorld commit SHA recorded.
- [ ] Results for both workspaces (fixture and RealWorld) are committed, including failures.
- [ ] The report states plainly whether the v1 goal was met, with the model id.
- [ ] The owner has reviewed the report; the review is recorded in this task's review log.

## Review log
