# 18 — Reference kind of a method group in a call chain

## What to build

`invocation` should mean that the row's method is the one being called. Today it means that the name lies inside the callee expression of any invocation, receiver chain included. The classifier in `src/CodeAtlas.Core/ScopedReferences.cs:105-107` takes the first ancestor invocation whose `Expression` span contains the use:

| Source | Use | Stored | Should be |
| --- | --- | --- | --- |
| `Host.Wrap(Host.Zero).Run()` | `Zero`, a method group | `invocation` | `reference` |
| `Host.Sink(nameof(Host.Target)).ToString()` | `Target`, which nothing calls | `invocation` | `reference` |
| `kinds.Select(StringOrNull).ToArray()` at `src/CodeAtlas.Cli/McpHost.cs:273` | `StringOrNull`, a method group | `invocation` | `reference` |

On the index of this solution 25 rows do this, as counted in technical review attempt 1 of [task 05](05-find-references.md). So `find_references` with `kinds: ["invocation"]` returns uses that are not calls.

Task 05 worded the contract for what is stored, and a seam test pins it. This task changes what is stored. It changes CLI rows too, so state the new definition in `docs/QUERY-CONTRACT.md` first. The kind is stored in each generation, and refresh reuses stored rows of unchanged files, so decide how an older generation gets the new kinds before the implementation.

`README.md` and `docs/USER-GUIDE.md` also describe the kinds. Check that they agree when this is done.

User stories: 7, 8. PRD: [Contract](../prd/0001-net-release.md#contract).

## Blocked by

- [05 — MCP tool find_references](05-find-references.md)

Status: ready

## Acceptance criteria

- [ ] The contract's `invocation` definition says the method is the callee, and lists no exception for a chain.
- [ ] MCP seam test: each case in the table is a `reference`, and a call of the method in the same chain stays an `invocation`.
- [ ] `McpFindReferencesTests.ANameInsideACalleeExpressionIsStoredAsAnInvocationEvenWhenNothingCallsIt` is changed to the new kinds.
- [ ] MCP and CLI rows stay equal for the same fixture.
- [ ] On the index of this solution, the 25 rows are `reference` rows, and each real call keeps `invocation`, including `.Run()` at the end of a chain.
- [ ] An index built before the change gets the new kinds, or the contract states when it does not.
- [ ] Qualification is green on Windows and Linux.

## Review log
