# Separate Angular and TypeScript engine

Milestone 12 does not add Angular, TypeScript, or SQL analysis to the Roslyn indexer. `CodeAtlas.Angular` stays empty. C# coverage is unchanged: constructors, accessors, local functions, and metadata-only methods stay outside the method query surface. No measured user request selected a further C# kind for this change.

## Boundary

A future Angular engine is a separate program with its own index, identities, and tests. It may read TypeScript and templates. It must not pretend that a C# search found an Angular screen. Cross-language links, when they exist, are explicit records with a C# symbol id and a template or TypeScript location, and they are absent until that engine has its own evidence.

SQL remains a later design decision. The C# tool continues to report `supportedLanguages: ["csharp"]` and the exclusions already stored in the index.

`coverage --index <manifest>` prints that boundary from the saved snapshot. A question about an Angular screen can only return C# candidates, and the response must not claim the screen was indexed.
