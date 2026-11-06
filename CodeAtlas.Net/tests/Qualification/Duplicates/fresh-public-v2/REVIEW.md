# Independent source-label review

Reviewer: `/root/finish_explanation_tests`, 2026-10-21. Scope: source and frozen labels only. I authored neither this corpus nor its labels, and did not implement the duplicate engine. I read `Corpus.cs`, `labels.json`, `FREEZE.json`, and `PROTOCOL.md`; I have not opened detector output, raw result files, measured similarities, or metrics for this corpus. I did not change any frozen file.

All three SHA-256 hashes match `FREEZE.json`. There are 16 source methods, 120 unique unordered labeled pairs, and no missing pair. Label counts: 1 exact, 1 renamed-near, 4 near-with-rule-differences, 1 boilerplate, 113 different.

## Findings

All 120 source labels are defensible under the protocol. No incorrect source label was found. This validates the labels, not detector performance or equivalence of arbitrary methods.

| Pair | Label assessment and source evidence |
| --- | --- |
| SquaresA / SquaresB | Valid exact method-body duplicate. Lines 7-9 and 13-15 use the same initial value, positive-only loop, multiplication, accumulator and final array-length subtraction. Neither body reads its containing method name or uses caller-information defaults. Useful for human consolidation review, with no automatic merge recommendation. |
| JoinA / JoinB | Valid renamed structural duplicate. Lines 19-21 and 25-27 change only bound parameter/local names (`parts`/`sections`, `result`/`joined`, `part`/`section`). Both initialize to `"begin"`, include only strings longer than two characters, call the same `string.Concat` and `Trim` operations with `":"`, and return `ToLowerInvariant()`. Renaming public parameters can affect named-argument callers, so extraction of a shared implementation still requires API review. |
| PathA / PathB | Valid rule-difference pair, never exact or renamed-equivalent. Line 33 contains the verbatim path prefix `@"c:\cache\"`; line 39 contains `"c:cache"`. With `name = "a"`, the returned strings are `c:\cache\a.data` and `c:cachea.data`. Backslashes are significant source/runtime characters. A similarity candidate must preserve the changed literal evidence. |
| WriteA / WriteB | Valid rule-difference pair. Lines 44-45 bind `Console.Write`; lines 50-51 bind `Console.WriteLine`. The latter emits line terminators. With `count = 0`, the first writes concatenated values and `finished`, while the second writes four separate lines. The member binding difference is observable behavior. |
| FeeA / FeeB | Valid business-rule-difference pair. Lines 55-56 use multiplier `0.03m` and threshold `500m`; lines 61-62 use `0.04m` and `700m`. With amount 600, the returned values are 26 and 24. Both differences must remain visible. |
| CheckedA / CheckedB | Valid rule-difference pair. Line 68 explicitly checks multiplication/addition overflow; line 74 explicitly disables those checks. For value 50000, the multiplication throws in CheckedA while CheckedB wraps and returns an integer. Matching operators and literals do not establish equivalent overflow semantics. |
| ReleaseA / ReleaseB | Valid negative despite identical XML summaries at lines 77 and 84. Lines 80-82 reject unapproved requests and require a bounded remaining amount; lines 87-89 accept every approved request and apply different arithmetic/comparisons otherwise. For `(approved: true, outstanding: 10)`, ReleaseA is false and ReleaseB is true. Same purported purpose does not imply duplicate behavior. |
| ForwardA / ForwardB | Valid boilerplate exclusion. Lines 91-92 are identical one-expression identity forwarders; they carry no nontrivial implementation to consolidate. Their short-body exclusion is a product policy, not a claim that their bodies differ. |

The other 112 `different` labels are all cross-family pairs: arithmetic array aggregation, string-array joining, string-path formatting, console side effects, decimal fee computation, integer checked arithmetic, Boolean release decisions, and integer identity forwarding. Each cross-family pair has a distinct signature, distinct operation sequence, or both. In particular, the equal-signature Checked/Forward cross-pairs are arithmetic transformations versus identity, not duplicates. No cross-family pair silently duplicates the two intended structural positives.

## Interpretation limits

`near-with-rule-differences` is a qualitative source label. This review does not assert that any such pair crosses a particular numerical similarity threshold; it asserts that a reported candidate must retain its differences and must never be promoted to exact/renamed equivalence. Whether it is useful to consolidate these pairs requires domain review and is correctly separate from useful duplicate precision/recall.

This small synthetic set has source-reviewed labels and a complete pair inventory. It is not statistically representative, a proof of detector generalization, a private-application usefulness review, or an independent human acceptance. Detector measurements must remain separately attributed and report omissions/caps and failures.
