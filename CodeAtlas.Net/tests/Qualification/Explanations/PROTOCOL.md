# M9 public generated-quality pilot, v1

Status: prepared and source-reviewed by the fixture/gold author; not executed. Root must accept the concrete criteria below before generation. No generated-quality, selection-improvement or runtime validation result exists yet. This is a small public synthetic pilot; it is not a hidden representative population or a substitute for required human acceptance.

The fixture contains eight selected project-qualified symbols in two package-free .NET 10 SDK projects. Both projects link the same Shared/Identity.cs source. It covers overload identity, selected local context, missing and correct XML, deliberately contradictory XML, delegation to an unseen implementation, an empty method body, method-group conversion, ambiguous business names and project-qualified linked-file identity. NuGet.Config clears remote sources; the locally installed pinned SDK/reference packs should suffice. Offline restore still needs to be executed and verified.

## Freeze and separation

Before generation, hash all fixture source/configuration, generation-cases.json, selection/input.json, review-only/gold.json and this protocol into FREEZE.json. Do not edit frozen inputs after observing responses. Preserve failures and first outputs; corrections or reruns are separately labeled.

The generator receives ONLY explicitly exported request JSON and the response contract/instructions. It must not open fixture source, Transport.cs, selection tasks, gold, reviewers' notes, other responses, or future evaluation outputs. Use one fresh generator turn per case, fixed instructions and unchanged default request budgets with referenceLimit 0. No provider is invoked by CodeAtlas. The root may delegate explicit local request-to-response generation to a coding agent. Record actual available provider/model identity without guessing unavailable model details, generation version, timestamps, elapsed time and token/cost availability. The generator may say unknown where metadata is not exposed; do not invent billing or tokenizer measurements.

The gold author did not generate responses and will review them. Review each response claim against the exact request before consulting the corresponding gold wording; record source references and all deviations. A fresh independent reviewer or human can additionally audit this adjudication. The authored gold is not an independent human acceptance.

## Proposed quality gate; root acceptance required before generation

1. Export, strict import and readback succeed for all eight cases with matching request/index/symbol/context identities; record contract failures separately from prose errors. All exported requests and outputs are hashed.
2. Zero wrong selected symbol, overload or project explanations. C1 must address the explicitly selected local adjusted.
3. Zero critical false or unsupported guarantees of transactionality, durable payment/persistence, security/authentication/authorization, network delivery or runtime execution. A statement merely quoting and identifying the author's disputed XML is not a behavior guarantee.
4. Review every claim and every factual assertion within it; compound claims cannot hide an unsupported assertion inside a mostly correct sentence. Source-observation assertions must be 100% source-supported. Interpretations must either be source-supported or clearly qualified as unresolved and consistent with available evidence. Count unsupported assertions, even when a quote is structurally valid. Do not count valid uncertainty as an unsupported guarantee.
5. All case-specific required observations in review-only/gold.json are present (8/8 cases). C3 explicitly distinguishes authored XML from code; C4 identifies unseen-callee uncertainty; C5 identifies the empty body; C6 does not confuse method-group return with invocation; C7/C8 preserve project identity.
6. Preserve ordinary semantic artifact hashes; optional prose must not alter source, XML/facts, index or retrieval ranking. Measure file bytes/time independently; report token/cost data unavailable if not actually exposed.

This is a strict smoke gate, not a statistical accuracy guarantee. If any gate fails, preserve the failure and restrict the reported claim. Do not change expected answers to accommodate output.

## Selection comparison

Use the same eight candidate IDs in the fixed candidateOrder and the same eight tasks in selection/input.json for both conditions. Baseline packets contain compact name/signature/project + authored XML + saved structural facts, without source excerpts or gold. Assisted packets contain byte-identical baseline fields plus the eight clearly labeled generated interpretations, with their caveats/citations. Do not add different candidate facts or reorder candidates across conditions.

Use fresh selectors for baseline and assisted conditions, blinded to gold, other condition packets/results and source files. Freeze packet hashes and equal response limits before selecting. Selectors return exactly one ID or abstention plus a reason per task. Review source-confirmed correctness and whether prose introduced an unsupported intent error; confident wording is not success.

Proposed selection gate: assisted correct selections >= baseline correct selections on the six positive tasks, all eight task outcomes reported, correct assisted abstention on both no-good-match tasks, zero new wrong-overload/project or critical invented-guarantee selections. Do not require a positive answer when the condition packet lacks evidence; record uncertainty/abstention as a missed positive when gold has a relevant method. Claim improvement only if assisted strictly improves the predeclared count without worsening abstention or critical errors. Equality permits a no-regression pilot result, not an uplift claim. A gate failure leaves selection acceptance open and must not promote AI prose into factual search evidence.

## Required retained output

Retain frozen hashes, actual CLI logs/index identity/tool version, all exact requests/responses, responder-reported provenance, first-run contract outcomes, per-assertion judgments and citations, limitations/severity, full selector packets and outputs, baseline/assisted counts and abstentions, reviewer roles and human acceptance status. No private application source or M7 question labels are used. Keep the fixture/gold and all generated artifacts local until explicit publication review.
