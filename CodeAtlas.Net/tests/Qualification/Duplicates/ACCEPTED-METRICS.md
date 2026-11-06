# M10 evaluation metrics accepted before measurement

Root accepted on 2026-10-21 before any qualification execution in this harness:

> Root ACCEPTS proposed metrics premeasurement exactly as stated plus existing frozen limits/resource gate.

Known public development corpus: enumerate all 66 pairs. Exact/renamed structural precision and recall must both equal 1 for the two labeled positives. Useful-review precision and recall must both equal 1, excluding the separately reported domain-review pair. No semantic literal/member difference may be emitted as exact/renamed. If the business-rule pair is emitted as near, retain differing constants and a manual investigation caveat. Boilerplate exclusions, unsupported cases, omissions and work caps must be explicit. The tiny known corpus cannot close the independent accuracy gate.

Fresh public corpus: the same complete-pair metrics, two positive pairs, semantic member/literal/checked/rule negative pairs, and two boilerplate methods. Frozen source/gold are authored after implementation but before executing the detector on them. Independent review remains required; no held-out claim follows from this author's labels.

Resource limits remain unchanged: at least three comparable extraction/scan runs; additional <=60 seconds and <=384 MiB process peak working set; catalog plus scan <=80 MiB; ordinary query median regression <=20% AND existing absolute M3/M4 budget. Corpus executions cannot substitute for large-solution resource measurements.

No threshold relaxation after observing results. Record failed outputs and create new configurations explicitly if implementation is repaired. Accepted executable and harness hashes are bound after compilation, before the corresponding detector execution.
