# Duplicate candidates

Milestone 10 groups method bodies after replacing identifiers with a placeholder. An exact group shares that normalized token hash. A near group has a token-set overlap of at least 0.85, a length difference of at most two tokens, and the same first token. Buckets larger than 30 are not pairwise compared.

`find-duplicates --index <manifest>` returns these groups. A group is a candidate for review. It is not proof that the methods behave the same, and CodeAtlas does not merge or delete application code. Similar names or documentation are not used.

Two tests cover a renamed identifier, a different expression, and a small edit.

On eConnector generation `fc8a59061ead4250966321e4afd3c00d`, `find-duplicates` returned **711 exact groups and 0 near groups**. A review of the first groups found real copies, including `TryParseCron` in the API and Services projects and repeated `GetUserId` helpers. It also found false groups: short bodies such as `Dispose`, `Cleanup` and `NormalizeEmail` share a normalized shape and were placed together. These groups are candidates only. They are not safe to merge.
