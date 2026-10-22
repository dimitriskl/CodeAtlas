# Optional local explanations

CodeAtlas stores generated descriptions separately from authored documentation and semantic snapshots. Select a provider/model with `configure-ai --provider <name> --model <name> [--effort <level>]`, then explicitly run `describe --index <manifest>`. The [user guide](USER-GUIDE.md#search-by-purpose-and-read-structural-facts) covers saved `appsettings.json` options, hidden key entry, environment variables, provider endpoints and overrides. Ollama runs locally without a key; cloud providers require one. With no configured selection or legacy credentials, AI is disabled. `--limit 10` generates a small batch; `--symbol <id>` selects one symbol.

Providers use their documented protocols: [OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [Azure OpenAI v1](https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle), [Anthropic Messages](https://platform.claude.com/docs/en/api/messages/create), [Kimi](https://platform.kimi.ai/docs/api/chat), [OpenRouter](https://openrouter.ai/docs/api/api-reference/chat/create-a-chat-completion), and [Ollama's OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility). Effort support varies by model. Connection tests send a fixed prompt, never project source; CodeAtlas does not discover models in the background, switch configured providers or download models automatically.

Only `describe` sends selected bounded source evidence. Explicit setup and selection overrides also call the provider with the fixed connection-test prompt. Model output supplies descriptions and selects evidence IDs. CodeAtlas constructs exact source anchors locally and validates the existing explanation contract. Anchors identify the supplied source context; they do not prove the generated prose follows logically from it. Unknown IDs are rejected. Credentials are read from environment variables and are not written into indexes, requests or descriptions. Configured provider calls can incur usage charges. Search, indexing and refresh never invoke a provider.

`search-symbols --mode assisted` includes current saved generated claims, with explicit AI provenance and source citations. `--mode names` excludes them. Stale, corrupt or old-generation cache entries are excluded from search. Missing descriptions fall back to the existing name/documentation/fact search. Searchable generated wording is an interpretation, not a verified fact.

The local coding-agent request/response workflow below remains available without provider credentials. Read the exported request before sharing it with an agent.

Setup can reuse `OPENAI_API_KEY` and other provider-specific environment keys already configured for another application. It leaves those original variables and the other application's files unchanged. After a successful cloud connection test, the credential is saved to a provider-specific `CODEATLAS_AI_*` Windows user environment variable. JSON contains only the selected provider, model, effort and endpoint.

## Request, respond, import

Provider selection in 0.00.28 passed 456 .NET tests and 39 Python tests on Windows. Live fixed-prompt connection tests passed for OpenAI using an existing environment key and for the installed Ollama `qwen3:8b` model with effort `none`. The OpenAI key was persisted at Windows user scope and verified absent from JSON settings. Tests cover the other provider protocols, key separation, failed-connection preservation and settings-backed CLI generation; Azure, Anthropic, Kimi and OpenRouter have no live validation in this change.

Version 0.00.27 passed all 429 .NET tests and 39 Python tests on Windows: 116 description-focused tests and 313 remaining tests. An initial stalled test run was stopped; the diagnostic rerun and remaining suite passed. A local test provider exercised the actual CLI path: no-key disablement, generation, validated storage, resumed batches and later search without credentials. Focused regressions cover class/method discovery, stale/corrupt cache exclusion, citation validation, Unicode anchor boundaries and description-aware pagination.

One approved live OpenAI call using an existing environment key reached response validation, but its evidence ID or source quote was invalid; nothing was accepted or saved. The automatic adapter now requests evidence IDs and constructs anchors locally. A subsequently approved retry generated and saved one class description in 6.679 seconds; assisted search matched its generated wording while names-only search returned no result, with CodeAtlas AI settings removed during both searches. This one-class test verifies the connection/storage/retrieval path, not general generated accuracy. These checks do not measure generated-description accuracy on eConnector.

Use a fresh verified manifest and copy the symbol ID from `find-symbol`. With the source checkout launcher:

```powershell
New-Item -ItemType Directory -Force '.codeatlas/agent-exchange' | Out-Null
$request = Join-Path (Get-Location) '.codeatlas/agent-exchange/request.json'
$response = Join-Path (Get-Location) '.codeatlas/agent-exchange/response.json'
dotnet run --project src/CodeAtlas.Cli --no-build -- explain-request --index $manifest --symbol $symbolId --output $request
# Ask your coding agent to read the request and write the response contract below.
dotnet run --project src/CodeAtlas.Cli --no-build -- explain-import --index $manifest --response $response
dotnet run --project src/CodeAtlas.Cli --no-build -- get-explanation --index $manifest --symbol $symbolId
```

Use a new request output filename for each export; existing files are not overwritten. For another solution, place exchange files under that solution's `.codeatlas/agent-exchange/`, or outside its source tree. Arbitrary JSON files placed in the source tree can be discovered as configuration inputs and make its index stale. Managed snapshot and explanation directories cannot be export destinations.

An exported request includes the exact selected declaration, authored XML and provenance, bounded relationship/reference evidence, optional supplied context, and source/dependency hashes. Every excerpt has an ID and source location. Add `--local <name>` for a supported variable declaration within the selected method; local descriptions are opt-in. `--context <solution-relative-file>` explicitly includes one additional file; use forward slashes. Conditional compilation without recorded parse context and unsupported declaration forms are rejected rather than guessed.

The responder must return strict JSON with these fields:

```json
{
  "contractVersion": 1,
  "requestId": "<copy from request>",
  "indexId": "<copy from request>",
  "symbolId": "<request.selection.symbolId>",
  "contextFingerprint": "<copy from request>",
  "provenance": {
    "provider": "<actual responder provider>",
    "model": "<actual model or honest unavailable identifier>",
    "generationVersion": "<responder prompt/version>",
    "generatedAtUtc": "<actual ISO-8601 timestamp>"
  },
  "claims": [{
    "text": "<source-grounded observation or qualified interpretation>",
    "category": "source-observation",
    "evidence": [{
      "evidenceId": "<request evidence id>",
      "offset": 0,
      "length": 1,
      "quote": "<exact cited substring>"
    }]
  }],
  "limitations": ["<missing context and uncertainty>"]
}
```

Replace every placeholder and example offset. Citation offsets and lengths are UTF-16 code units relative to the excerpt's `text`, not byte offsets or whole-file offsets. Claim categories are `source-observation` or `interpretation`; every claim needs matching evidence. Duplicate/unknown properties, malformed identities, mismatched quotes and oversized responses are rejected. The canonical registered request is authoritative; changing the exported copy cannot expand accepted evidence.

Default limits: six files, 64 KiB context, 128 KiB request, 64 KiB response, 1 MiB per source file and 32,768 estimated tokens. `--max-files`, `--max-context-bytes`, `--max-request-bytes`, `--max-response-bytes`, `--max-source-bytes`, `--max-estimated-tokens` and `--reference-limit` accept bounded overrides; reference limit is 0–8 (default two). Token estimates are a local size guard, not a tokenizer, billing estimate or provider spending limit. The manual workflow makes no provider calls. Ctrl+C cancels pending work.

## Freshness and recovery

Reads also accept `--request <requestId>` instead of `--symbol`. A source-stale semantic index refuses export/import/reads. A new index generation marks old explanations stale, even when a symbol ID survives. Changes to explicitly supplied context invalidate its interpretation without claiming the context was a semantic dependency. Returned states distinguish missing, stale, unverifiable and corrupt/unreadable cache from current-for-observed-inputs. Partial input coverage remains partial.

Registered requests and accepted entries live under `.codeatlas/explanations/<solution-key>/`, outside immutable generations. Ordinary indexing and factual queries do not depend on them. Each derived file is atomically replaced, but the accepted entry and latest-symbol pointer are separate commits. A crash or I/O failure between them can leave the request readable while symbol lookup still shows its previous entry. Retry importing the same verified response to repair that pointer. A failed external export can leave an unused registered request. Derived files can be regenerated; never edit snapshot hashes to repair them.

Developer XML, structural facts and AI interpretations remain separate. Provider/model metadata is responder-reported and unauthenticated. Exact quote validation proves traceability, not that a claim follows from a quote. The contract tests deliberately include a traceable unsupported intent claim to preserve this distinction. Generated accuracy and actual symbol-selection usefulness require a separate reviewed evaluation; contract tests alone do not establish either.

## Public pilot evidence — 21 October 2026

A frozen public eight-symbol pilot used fresh coding-agent responder contexts that saw only one exported request each. Independent review judged 20 claims and 65 decomposed factual assertions, with zero unsupported assertions or critical invented guarantees and all 13 required observations covered. All eight strict imports/readbacks passed, exact citations matched and immutable semantic artifacts remained unchanged. Model identifiers, token usage, billing and overall generation duration were unavailable; provider/model labels remain self-reported.

Separate blinded selectors answered six positive and two unsupported-claim tasks using identical ordered baseline fields. Documentation/facts alone yielded 3/6 positive selections; adding the generated interpretations and exact cited quotations yielded 6/6. Both conditions correctly abstained on 2/2 negative tasks, with zero identity or critical-guarantee errors. This is a small synthetic, source-authored and agent-reviewed pilot, not human accuracy approval or statistical evidence. The added quotations prevent attributing the gain solely to prose. Generated text remains optional. Current assisted search includes validated saved descriptions; this historical pilot did not measure that search integration.

[Public fixtures, frozen gold and reproduction protocol](../tests/Qualification/Explanations/README.md) are checked in. Exact requests, first responses, binary hashes, contract outputs and both independent reviews remain in the ignored `.codeatlas/repair-v23/m09-quality/` evidence directory. The request-export binary preceded the final search-only language-boundary correction; recorded payload hashes distinguish those builds rather than treating the same version label as identical binaries.
