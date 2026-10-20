# Duplicate candidates

Milestone 10 groups method bodies after replacing identifiers with a placeholder. An exact group shares that normalized token hash. A near group has a token-set overlap of at least 0.85, a length difference of at most two tokens, and the same first token. Buckets larger than 30 are not pairwise compared.

`find-duplicates --index <manifest>` returns these groups. A group is a candidate for review. It is not proof that the methods behave the same, and CodeAtlas does not merge or delete application code. Similar names or documentation are not used.

Two tests cover a renamed identifier, a different expression, and a small edit. No labeled false-positive study on eConnector has been run.
