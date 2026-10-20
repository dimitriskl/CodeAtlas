# Structural facts and purpose search

Milestone 7 adds Roslyn facts and lexical search on top of the documentation catalog. It does not call a model and it does not claim runtime behavior.

## Facts

Each supported symbol can record its containing type, base type, direct interfaces, implemented or overridden members, declared parameter/return/property/field types, and resolved explicit invocations inside ordinary method bodies. A resolved invocation is a source binding to a canonical declaration. It is not proof of business purpose, effects, or which override runs at runtime.

Local functions, anonymous functions, constructors, accessors, method groups, dynamic or unresolved calls, reflection, dependency injection, and virtual dispatch stay outside these facts. Unresolved invocations are recorded as issues. Existing `find-references` results are unchanged and are not the same collection.

`get-facts --index <manifest> --symbol <id>` returns one symbol's facts from a saved index. Schema 1 and 2 indexes answer older queries and ask for a rebuild before fact or purpose commands.

## Search

`search-symbols --query <text>` tokenizes names and signatures. `--mode assisted` is the default and also uses authored documentation plus documentation reached through one containing-type, implementation, override, or invocation link. `--mode names` uses only names and signatures. Ranking version `lexical-distinct-tfidf-v1` explains every score. Weak or empty overlap is reported as an abstention instead of a guessed match. `--language` accepts `csharp` only.

## Evidence

The Debug build passed **236 .NET tests** serially and **23 Python tests**. The new tests cover overload and interface identity, cross-project calls, linked fields, edit refresh of a binding, tokenization, documentation-assisted ranking, tie pages, and abstention. The approved eConnector development questions have not been scored on a schema 3 index yet, so this file does not claim a retrieval improvement on that application.
