# 13 — macOS SqliteStoreTests fail with "database table is locked"

## What to build

Find why the `SemanticSolution` fixture can fail on macOS with Roslyn's "database table is locked", and stop it.

In run 41025858906 attempt 1 (macos-latest), 18 `SqliteStoreTests` failed at once, 17 s into `dotnet test`. Each failure is the class fixture: `SemanticSolution.InitializeAsync` (`tests/CodeAtlas.Tests/SemanticTests.cs:25`) calls `SolutionAnalysis.CreateIndexAsync`. Reference finding (`ScopedReferences.cs:53`) reaches Roslyn's `SQLitePersistentStorage`, which throws `SqlException: database table is locked: DocumentData7` from `InsertOrReplaceBlobIntoWriteCache`. The re-run of the failed job passed. It has not happened on Windows or Linux.

What is known:

- `MSBuildWorkspace` uses Roslyn's SQLite persistent storage. The store is keyed by process name and solution path (on Windows: `%LOCALAPPDATA%\Microsoft\VisualStudio\Roslyn\Cache\testhost.exe-<hash>\<solution>-<hash>`). Its write cache is a shared-cache in-memory database, so two storage instances for one path share tables.
- Four test classes (`SemanticTests`, `IncrementalGateTests`, `ScopedReferencePartitionTests`, `SqliteStoreTests`) each create their own `SemanticSolution`. All four restore and index the same in-repo `tests/SemanticFixtures/Semantic.slnx`. xUnit 2.9 with `maxParallelThreads` 1 still starts every test collection, and the `await`s of their async fixtures interleave, so these analyses can overlap in one testhost.
- A local probe on Windows ran 4 concurrent `CreateIndexAsync` calls on that one path, 8 times. None failed. So the overlap is a likely cause, not a proven one.

If the overlap is the cause, the fix is test-only: give each `SemanticSolution` its own copy of the fixture in a temp folder, as `IndexedSolution` does for `Sample.slnx`. If one analysis can hit the lock on its own, a real `index` command can fail too. Then the fix is in production (for example, turn off Roslyn persistent storage in `SolutionAnalysis`) and needs the owner's approval.

Do it in this order:

1. Reproduce on macos-latest. For example, a temporary manual workflow job that loops the `SqliteStoreTests` fixture, or concurrent and single `CreateIndexAsync` calls on `Semantic.slnx`.
2. Fix the proven cause and keep a test that guards it.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: deferred until after the release

## Acceptance criteria

- [ ] The review log names the proven cause, with the reproduction and its result on macos-latest.
- [ ] The fix removes that cause. If it is test-only, each `SemanticSolution` analyzes its own solution path. If it needs production code, the owner approved it first.
- [ ] Qualification is green on Windows, Linux and macOS.
- [ ] No change to CLI output, exit codes or schema.

## Review log
