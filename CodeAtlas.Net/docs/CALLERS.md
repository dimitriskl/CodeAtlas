# Caller navigation

Milestone 11 walks resolved invocation facts from `get-facts`. `find-callers --index <manifest> --symbol <id> --depth <1-5>` returns the methods whose bodies bind to that symbol, then their callers, up to the depth and a limit of 100 edges. A repeated method is recorded as a cycle and is not expanded again.

These edges are source bindings. They describe potentially affected code. They do not prove runtime dispatch or that a change will break a caller. Method groups, local functions, and unresolved calls stay out because they are not resolved invocation facts.

One test covers direct callers, a cycle, and the edge limit.
