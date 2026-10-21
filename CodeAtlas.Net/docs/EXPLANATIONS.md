# Optional local explanations

CodeAtlas exports bounded context for a selected source symbol. Your coding agent writes a local response; CodeAtlas validates and stores it separately from semantic snapshots. CodeAtlas never calls a provider or uploads source. Read the request before sharing it with an agent.

## Request, respond, import

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

Default limits: six files, 64 KiB context, 128 KiB request, 64 KiB response, 1 MiB per source file and 32,768 estimated tokens. `--max-files`, `--max-context-bytes`, `--max-request-bytes`, `--max-response-bytes`, `--max-source-bytes`, `--max-estimated-tokens` and `--reference-limit` accept bounded overrides; reference limit is 0–8 (default two). Token estimates are a local size guard, not a tokenizer, billing estimate or provider spending limit. CodeAtlas invokes no paid service. Ctrl+C cancels pending work.

## Freshness and recovery

Reads also accept `--request <requestId>` instead of `--symbol`. A source-stale semantic index refuses export/import/reads. A new index generation marks old explanations stale, even when a symbol ID survives. Changes to explicitly supplied context invalidate its interpretation without claiming the context was a semantic dependency. Returned states distinguish missing, stale, unverifiable and corrupt/unreadable cache from current-for-observed-inputs. Partial input coverage remains partial.

Registered requests and accepted entries live under `.codeatlas/explanations/<solution-key>/`, outside immutable generations. Ordinary indexing and factual queries do not depend on them. Each derived file is atomically replaced, but the accepted entry and latest-symbol pointer are separate commits. A crash or I/O failure between them can leave the request readable while symbol lookup still shows its previous entry. Retry importing the same verified response to repair that pointer. A failed external export can leave an unused registered request. Derived files can be regenerated; never edit snapshot hashes to repair them.

Developer XML, structural facts and AI interpretations remain separate. Provider/model metadata is responder-reported and unauthenticated. Exact quote validation proves traceability, not that a claim follows from a quote. The contract tests deliberately include a traceable unsupported intent claim to preserve this distinction. Generated accuracy and actual symbol-selection usefulness require a separate reviewed evaluation; contract tests alone do not establish either.

## Public pilot evidence — 21 October 2026

A frozen public eight-symbol pilot used fresh coding-agent responder contexts that saw only one exported request each. Independent review judged 20 claims and 65 decomposed factual assertions, with zero unsupported assertions or critical invented guarantees and all 13 required observations covered. All eight strict imports/readbacks passed, exact citations matched and immutable semantic artifacts remained unchanged. Model identifiers, token usage, billing and overall generation duration were unavailable; provider/model labels remain self-reported.

Separate blinded selectors answered six positive and two unsupported-claim tasks using identical ordered baseline fields. Documentation/facts alone yielded 3/6 positive selections; adding the generated interpretations and exact cited quotations yielded 6/6. Both conditions correctly abstained on 2/2 negative tasks, with zero identity or critical-guarantee errors. This is a small synthetic, source-authored and agent-reviewed pilot, not human accuracy approval or statistical evidence. The added quotations prevent attributing the gain solely to prose. Generated text remains optional and does not feed factual search ranking.

[Public fixtures, frozen gold and reproduction protocol](../tests/Qualification/Explanations/README.md) are checked in. Exact requests, first responses, binary hashes, contract outputs and both independent reviews remain in the ignored `.codeatlas/repair-v23/m09-quality/` evidence directory. The request-export binary preceded the final search-only language-boundary correction; recorded payload hashes distinguish those builds rather than treating the same version label as identical binaries.
