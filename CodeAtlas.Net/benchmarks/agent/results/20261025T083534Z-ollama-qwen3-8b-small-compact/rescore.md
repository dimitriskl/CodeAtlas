# Rescore: ollama-qwen3-8b-small-compact

Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.

| Mode | Runs | Old success | New success | New via lenient parse |
| --- | ---: | ---: | ---: | ---: |
| plain | 15 | 1 | 4 | 3 |
| codeatlas | 15 | 4 | 7 | 3 |

| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| find-coupon-expiry | plain | 1 | False | True | file | backslashes+json-object | - |
| find-coupon-expiry | codeatlas | 1 | False | True | file | double-encoded+json-object | - |
| impact-payment-processors | codeatlas | 1 | False | False | file | double-encoded+json-object | symbols: {"missing": ["SepaPaymentProcessor", "WirePaymentProcessor"], "forbidden": []} |
| impact-payment-processors | plain | 1 | False | False | file | backslashes+json-object | symbols: {"missing": ["SepaPaymentProcessor", "WirePaymentProcessor"], "forbidden": []} |
| impact-open-orders-callers | plain | 1 | False | False | - | - | files: {"detail": "answer.files is not a list"} |
| impact-open-orders-callers | codeatlas | 1 | False | False | file | escaped-quotes+backslashes+json-object | files: {"missing": ["shop.app/customerstatementservice.cs", "shop.app/reminderjob.cs"], "forbidden": []} |
| find-coupon-expiry | codeatlas | 2 | False | True | file | double-encoded+json-object | - |
| find-coupon-expiry | plain | 2 | False | False | final-text | markdown-fence+json-object | files: {"missing": ["shop.core/discounts/couponvalidator.cs"], "forbidden": []}; symbols: {"missingGroups": [["CouponValidator.IsExpired"]]} |
| impact-payment-processors | plain | 2 | False | False | file | escaped-quotes+backslashes+json-object | symbols: {"missing": ["SepaPaymentProcessor", "WirePaymentProcessor"], "forbidden": []} |
| impact-payment-processors | codeatlas | 2 | False | True | file | layout+json-object | - |
| impact-open-orders-callers | codeatlas | 2 | False | False | file | double-encoded+json-object | files: {"missing": ["shop.app/customerstatementservice.cs", "shop.app/reminderjob.cs"], "forbidden": []} |
| impact-open-orders-callers | plain | 2 | False | False | file | escaped-quotes+backslashes+json-object | files: {"missing": ["shop.app/customerstatementservice.cs", "shop.app/reminderjob.cs"], "forbidden": []} |
| find-coupon-expiry | plain | 3 | False | True | file | backslashes+json-object | - |
| find-coupon-expiry | codeatlas | 3 | True | True | file | json-object | - |
| impact-payment-processors | codeatlas | 3 | False | False | - | - | symbols: {"detail": "answer.symbols is not a list"} |
| impact-payment-processors | plain | 3 | False | False | file | backslashes+json-list | symbols: {"missing": ["CardPaymentProcessor", "PayPalPaymentProcessor", "SepaPaymentProcessor", "WirePaymentProcessor"], "forbidden": []} |
| impact-open-orders-callers | plain | 3 | False | True | file | backslashes+json-list | - |
| impact-open-orders-callers | codeatlas | 3 | False | False | file | backslashes+json-list | files: {"missing": ["shop.app/customerstatementservice.cs", "shop.app/reminderjob.cs"], "forbidden": []} |
