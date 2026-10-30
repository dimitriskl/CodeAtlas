# 14 — search_symbols names mode and abstention

## What to build

Lexical search over names, signatures and documentation (`mode: names`).

Search abstains when evidence is weak, or when the question is outside Angular/TypeScript (for example, it names C# types).

User stories: 17, 18, 19. PRD: [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [05 — Angular declarations](05-angular-declarations.md)
- [12 — get_documentation](12-get-documentation.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: a query for a purpose word in a JSDoc summary ranks that symbol first.
- [ ] MCP seam test: a nonsense query abstains, with a documented decision value.
- [ ] MCP seam test: a C#-only question abstains as outside the supported language.
- [ ] Results come from a frozen label set for the fixture, and a regression fails the test.
- [ ] `MCP-CONTRACT.md` has the `search_symbols` section.

## Review log
