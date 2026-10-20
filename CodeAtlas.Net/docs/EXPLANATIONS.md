# Optional local explanations

Milestone 9 stores an explanation only after an external agent writes a response file. CodeAtlas does not call a model and does not upload source.

`explain-request` writes the symbol id, snapshot id, signature, documentation status, and a content hash. `explain-import` accepts a response with the same ids and hash, plus `text` and `model`, and stores it under `.codeatlas/explanations/`. A changed symbol or snapshot makes a later read `stale` and rejects a mismatched import. Indexing and factual queries work when no explanation exists.

One focused test covers import, a current read, a stale read, and rejection of a changed symbol. This is not an accuracy evaluation of generated text.
