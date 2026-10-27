# Rescore: ollama-qwen3-8b-small-smoke2

Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.

| Mode | Runs | Old success | New success | New via lenient parse |
| --- | ---: | ---: | ---: | ---: |
| plain | 2 | 0 | 1 | 1 |

| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| find-coupon-expiry | plain | 1 | False | True | file | backslashes+json-object | - |
