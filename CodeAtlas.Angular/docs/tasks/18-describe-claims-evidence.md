# 18 — describe with claims and evidence

## What to build

`codeatlas-ng describe --limit` describes only these kinds: component, directive, pipe, service, guard, interceptor, class, function, method.
- Provider output is validated as claims with evidence, and stored with provenance.
- All six provider clients are tested against fakes at the HTTP boundary.
- A provider failure stops the batch, and saved work is kept.
- MCP queries never call a provider.

User stories: 22, 30, 39, 41, 42, 60. PRD: [AI descriptions](../prd/0001-initial.md#ai-descriptions). [ADR 0003](../adr/0003-descriptions-survive-refresh.md).

## Blocked by

- [05 — Angular declarations](05-angular-declarations.md)
- [17 — configure-ai and the shared credential resolver](17-configure-ai-credentials.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: `describe --limit 2` stores 2 descriptions with provenance (provider, model, generation version).
- [ ] CLI seam test: a property, input or route is never sent to a provider.
- [ ] CLI seam test: a response with a claim citing unknown evidence is rejected and not stored.
- [ ] CLI seam test: a fake provider error on item 2 stops the batch, keeps item 1, and exits non-zero.
- [ ] A test for each of openai, azure, anthropic, kimi, ollama and openrouter, against a fake HTTP server.
- [ ] MCP seam test: no query triggers an HTTP call to any provider.

## Review log
