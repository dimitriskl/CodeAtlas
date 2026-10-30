# ADR 0003: AI descriptions survive refresh while the symbol is unchanged

- Status: Proposed
- Date: 2026-10-27
- Author: Dimitris Klis

## Context

In the .NET edition, descriptions are keyed to an index generation and a context fingerprint: "A new index generation marks old explanations stale, even when a symbol ID survives" (`CodeAtlas.Net/docs/EXPLANATIONS.md`). So every refresh means descriptions have to be generated again (`CodeAtlas.Net/docs/USAGE.md`), which costs money and time on codebases that are edited often.

## Decision

1. AI descriptions are optional.
   - They are generated only by the CLI `describe` command, never by MCP queries.
   - Kinds covered: component, directive, pipe, service, guard, interceptor, class, function, method. Not covered: property, input, output, interface, type-alias, enum, route.
2. Providers:
   - The same six as .NET: openai, azure, anthropic, kimi, ollama, openrouter.
   - Settings are kept in `.codeatlas/appsettings.json`, the same file .NET uses.
   - Credentials are shared with the .NET edition, so a key saved once works in both:
     - the same environment variables in the same order, including the Windows user-scope value;
     - then the same OS keychain entries, read and written by both editions;
     - then the .NET fallback credentials file, read-only.
   - With no keychain available, `configure-ai` stops with an error telling the user to set `CODEATLAS_AI_<PROVIDER>_KEY`. It saves no key, but saves the other settings.
   - The exact names are listed in PRD 0001, Further Notes.
   - HTTPS is required except on loopback. The `configure-ai` test prompt contains no source.
3. Output: claims with evidence, plus provenance (provider, model, generation version).
4. Validity: a description stays valid across refresh until one of these changes:
   - the symbol's own source text;
   - its JSDoc/TSDoc;
   - for a component, its template;
   - the signatures of the symbols its evidence cites.

   Other edits in the same file do not count. Stale descriptions are excluded from search and reported as stale. A provider failure stops the batch and keeps saved work.
5. On refresh, valid descriptions are carried into the new generation.

## Consequences

- Refresh no longer throws away paid work for unchanged symbols.
- The context is limited to the four inputs above. A change in behaviour reached only through uncited code does not make a description stale. That is an accepted trade-off for keeping descriptions across refreshes.
- This differs from C#. `MCP-CONTRACT.md` must state that description validity is edition-specific.
- Tests must prove three things:
  - an unchanged symbol keeps its description;
  - each of the four inputs makes it stale;
  - another edit in the same file does not.

## Alternatives considered

- **Generation-bound validity, like .NET.** Rejected: it forces re-description after every refresh.
- **Never invalidate automatically.** Rejected: it serves descriptions that no longer match the code.
- **Invalidate on any change in the same file.** Rejected: it is too coarse for large component files.
