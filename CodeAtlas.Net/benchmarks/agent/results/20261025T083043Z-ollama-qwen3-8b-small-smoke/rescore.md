# Rescore: ollama-qwen3-8b-small-smoke

Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.

| Mode | Runs | Old success | New success | New via lenient parse |
| --- | ---: | ---: | ---: | ---: |
| plain | 1 | 0 | 0 | 0 |
| codeatlas | 1 | 1 | 1 | 0 |

| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| find-coupon-expiry | codeatlas | 1 | True | True | file | json-object | - |
| find-coupon-expiry | plain | 1 | False | False | final-text | text-fallback | files: {"missing": ["shop.core/discounts/couponvalidator.cs"], "forbidden": []}; symbols: {"missingGroups": [["CouponValidator.IsExpired"]]} |
