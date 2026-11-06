# Symbol documentation

Milestone 6 is accepted in 0.00.14. CodeAtlas stores developer XML documentation for source types, ordinary methods, properties, and fields. It does not generate wording and it does not call a language model.

## What is stored

Each supported symbol keeps its Roslyn documentation id, project, signature, and source locations. Documentation comments stay attached to the declaration that owns them. Ordinary comments and comments whose owner cannot be established stay out of the catalog. Sections keep the author's XML and the extracted text. Inherited sections record the source symbol and the resolution path. Missing, malformed, cyclic, unresolved, and over-depth documentation stay explicit. External XML includes are not expanded.

Constructors, accessors, local functions, local variables, events, metadata-only symbols, and synthesized members other than a top-level-statement `Program` type are excluded and named in `documentationCoverage`. A synthesized `Program` type is included so the catalog matches the type inventory; it stays missing when the file has no authored documentation.

`find-symbol` without `--kind`, and `--kind method`, still return the existing method records. Other kinds return a compact summary whose preview is at most 1000 characters. `get-documentation` returns the full authored record. Reference queries reject a known type, property, or field id with `unsupported_symbol_kind`.

Schema 2 indexes require the catalog. Schema 1 indexes still answer method and reference queries and report that documentation features need a rebuild.

## Tests

The Debug build passed **228 .NET tests** with `xUnit.MaxParallelThreads=1` and **23 Python tests**. The .NET count includes the documentation catalog, CLI contract, refresh, and validation tests: identity across overloads, partials, generics, and linked files; missing and unowned comments; inheritance and renamed parameters; cycles and malformed XML; schema 1 compatibility; stale documentation; and cancellation before publication. Two parallel test threads stopped making progress in the combined suite, so the accepted run is serial. After including synthesized top-level `Program` types, the catalog, validation, CLI, and refresh tests passed again (59 tests) and indexing `CodeAtlas.slnx` succeeded.

## `<project_name>` measurement — 20 October 2026

One full index of `<project_name>.sln` with tool **0.00.14** finished in **563.167 seconds**. Reference collection was 512.974 seconds. Documentation collection was **1.419 seconds**. The run kept the previous counts: 30 projects, 3,943 types, 16,979 methods, and 56 warnings. The new catalog has **40,319 symbols**: 16,979 methods, 3,943 types, 13,299 properties, and 6,098 fields. Documentation status is 17,332 present, 22,618 missing, 354 unresolved, and 15 malformed. Projects, types, methods, and references are identical to the retained milestone 4 index `53ac9c8ec6dc4b128fff8318a9a5d7f7`. Generation `7f0636ff867141c1aef549726028051d` is that run. It predates the top-level `Program` agreement fix; `<project_name>`'s type inventory already matched, so the fix does not change this solution's catalog. Target Git status still shows only the untracked `.codeatlas/` directory. This is one run, not a repeated performance budget.
