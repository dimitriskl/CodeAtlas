# ADR 0002: Separate TypeScript engine and index

- Status: Proposed
- Date: 2026-10-27
- Author: Dimitris Klis

## Context

`CodeAtlas.Net/docs/ANGULAR-ENGINE.md` says: "A future Angular engine is a separate program with its own index, identities, and tests. It may read TypeScript and templates. It must not pretend that a C# search found an Angular screen."

The .NET edition reports `supportedLanguages: ["csharp"]`. It abstains on Angular and TypeScript questions, and it does not track `.ts` or `.html` files for freshness. Roslyn cannot analyze TypeScript or Angular templates. The TypeScript compiler and the Angular compiler can.

## Decision

1. CodeAtlas Angular is a separate TypeScript/Node program in `CodeAtlas.Angular/`.
2. Toolchain:
   - Node 24 LTS, TypeScript in strict mode, ESM;
   - the TypeScript compiler API and `@angular/compiler-cli`;
   - `@modelcontextprotocol/sdk` (stdio), `better-sqlite3`;
   - Vitest, ESLint with Prettier, and npm with a committed lockfile.
3. Scope:
   - It analyzes Angular CLI workspaces (`angular.json`).
     - Angular 17 and later: fully supported.
     - Angular 15 and 16: indexed best-effort, with template coverage marked `partial`.
     - Below 15: refused with `unsupported_angular_version`.
     - No `angular.json`: refused with `workspace_not_found`.
   - Both standalone components and NgModules are supported. Signal inputs, outputs and models are treated like their decorator forms.
4. Indexed as symbols:
   - `.ts` files, including spec files;
   - inline and `.html` templates, for component, input, output, pipe and directive usage.
5. Freshness:
   - Hashed, and a change makes the index stale:
     - every indexed source and template file;
     - `angular.json`, `tsconfig*.json` and `package.json` (also used for structure);
     - `package-lock.json`.
   - Styles and assets are hashed, but a change only adds a warning and never makes the index stale.
   - `node_modules` is excluded entirely and never hashed.
6. Storage:
   - Indexes live in the analyzed repository under `.codeatlas/workspaces/<workspace-name>-<path-hash>/`.
   - Each index is a set of immutable SQLite generations with a `current.json` pointer.
   - A writer lock prevents concurrent publishing.
   - After each publish, the current generation and the 2 previous ones are kept, and older ones are deleted.
   - In v1, `refresh` is a full re-index. It publishes only if something changed. Incremental refresh comes after v1, and only if the benchmark shows it is needed.
   - Every query hashes the tracked files and refuses a stale index.
   - The SQLite schema is the edition's own and is not shared with .NET.
7. Symbol id: `<angular.json project>::<workspace-relative file path>#<qualified name>`.
   - The id has no signature. Overloads share one id and list their signatures.
   - Paths always use forward slashes and are workspace-relative.
   - The exact formats for the path hash, the workspace name and route/nested qualified names are deterministic and specified in `MCP-CONTRACT.md`.
8. Release and versioning:
   - Its own version line, `x.xx.xx`, starting at `0.00.01`, bumped per release.
   - Its own CI workflow, on `angular/main` and on PRs to it. It runs on Windows Server 2025 and Ubuntu 24.04 with Node 24: lint, type-check, tests and a reproducible npm pack.
   - It runs in parallel with .NET and never blocks it.
9. Deferred: Nx workspaces, and C#↔Angular cross-language links.

## Consequences

- The C# boundary stays true. Each edition answers only for its own language.
- Identities differ between editions, so cross-language links will need explicit, evidence-backed records later.
- Two codebases share concepts and the envelope, not code. Contract drift is prevented by the shared contract document and contract tests.
- Angular 15 and 16 support is best-effort and is reported in coverage, not hidden.

## Alternatives considered

- **Extend the Roslyn indexer.** Rejected: Roslyn cannot read TypeScript or templates, and it contradicts `ANGULAR-ENGINE.md`.
- **Share one SQLite schema across editions.** Rejected: the symbol models differ, and every schema change would couple the two release lines.
- **Use a TypeScript language server (LSP) as the engine.** Rejected: it has no template-usage, route or injection reference model, and no persisted, versioned index.
- **Support Nx in v1.** Deferred, to keep v1 small.
