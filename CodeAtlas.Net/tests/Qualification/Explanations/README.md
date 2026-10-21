# Public explanation qualification inputs

This directory contains the public synthetic source, configuration, generation cases, selection tasks and review gold used to prepare the optional explanation pilot. It contains no application source, private retrieval questions, runtime index, input manifest, environment digest, generated response or local execution log. No provider API is called by these files or by CodeAtlas.

The fixture and gold have the same author. `PROTOCOL.md` specifies a separate request-only generator and independent response/selection review; authored gold is not independent human acceptance. The retained protocol and `FREEZE.json` describe their original pre-generation state. They are historical inputs, not a claim that a new checkout has completed qualification. Results and release evidence belong in the project documentation.

## Contents and integrity

`fixture/Quality.slnx` contains two package-free .NET 10 projects. Both link `Shared/Identity.cs`, making project-qualified identity observable. Eight selections exercise overloads, a selected local, authored/missing/contradictory XML, an unseen callee, an empty body, a method group and linked source. `generation-cases.json` selects methods; `selection/input.json` holds the fixed tasks/order; `review-only/gold.json` is adjudication material which generators/selectors must not open.

Every file listed in `FREEZE.json` is copied byte-for-byte. Check those hashes before any run. New instructions in this README are outside the original freeze. Do not revise frozen source or labels after observing outputs; preserve failed first responses and label reruns separately.

## Manual export, generation and review

Run commands from `CodeAtlas.Net` with the pinned SDK installed and the CLI built. The example uses PowerShell and the Debug CLI; an installed `codeatlas` can perform the same commands.

```powershell
$cli = Join-Path (Get-Location) 'src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll'
$pilot = Join-Path (Get-Location) 'tests/Qualification/Explanations'
$solution = Join-Path $pilot 'fixture/Quality.slnx'
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed' }
# The first restore creates package lock files; subsequent restores can use --locked-mode.
$indexed = dotnet $cli index --solution $solution | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Fixture indexing failed' }
$exchange = Join-Path $pilot 'fixture/.codeatlas/agent-exchange'
New-Item -ItemType Directory -Path $exchange -Force | Out-Null
$cases = Get-Content -Raw (Join-Path $pilot 'generation-cases.json') | ConvertFrom-Json
foreach ($case in $cases.cases) {
    $request = Join-Path $exchange ($case.caseId + '-request.json')
    $arguments = @($cli, 'explain-request', '--index', $indexed.index,
        '--symbol', $case.symbolId, '--output', $request, '--reference-limit', '0')
    if ($case.local) { $arguments += @('--local', $case.local) }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw ('Export failed: ' + $case.caseId) }
}
```

Before generation, record the protocol/source/gold hashes and explicitly accept the unchanged criteria. Record the CLI version, exact executable hashes and immutable generation identity separately under ignored `.codeatlas/`; do not commit runtime artifacts. The fixture clears NuGet feeds and expects the pinned SDK/reference packs locally. Restore generates package locks and build output; those are not part of the original frozen input set and should not be accidentally staged.

Give a fresh, explicitly chosen coding agent only one exported request and its response instructions at a time. It must not open fixture source, `Transport.cs`, tasks, gold or other outputs. Save the first response as `<case>-response.json` under the ignored exchange directory. Record actual available provider/model provenance and elapsed time; state when token/cost measurements are unavailable. There is no automatic model invocation or source upload in this workflow.

For each explicitly produced response, run the strict import and readback using that response's actual request ID:

```powershell
dotnet $cli explain-import --index $indexed.index --response (Join-Path $exchange 'C1-response.json')
dotnet $cli get-explanation --index $indexed.index --request '<requestId-from-C1-request.json>'
```

Repeat for C2–C8. Keep all original outputs and failures. A successful strict import checks contract/evidence integrity; it does not establish prose accuracy. An independent reviewer must adjudicate every assertion against the request and frozen gold, check all required observations and unsupported guarantees, and verify that optional prose did not modify semantic artifacts. Follow all six criteria in `PROTOCOL.md`.

For the selection comparison, build two packets with the exact same eight candidate IDs/order and task wording. Baseline contains compact names/signatures/project, authored XML and saved facts. Assisted adds only the clearly labeled generated interpretations and citations. Use fresh selectors blinded to source/gold and the other condition. Report all eight task outcomes, abstentions and errors. Equality is a no-regression result; only a strict count improvement with the other criteria preserved supports an uplift claim on this small pilot.
