# Agent benchmark: ollama-qwen3-8b-small-compact-v2

- Target: shop  (`16f89b6822e62675c3be73acb72ee199194dc57e`)
- Agent: Claude Code 2.1.310 (Claude Code), provider ollama, model `qwen3-8b-32k`, 3 repetition(s), started 20261025T100837Z
- Suite: small; agent: compact (short system prompt, minimal tools)
- Ollama 0.40.10 at http://localhost:11434: num_ctx 32768; local model, cost recorded as 0
- CodeAtlas: 0.00.32; upfront index 7.1 s, no model cost
- LSP mode: unavailable: csharp-ls is not installed (dotnet tool install --global csharp-ls); the LSP mode is skipped

| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| plain | 5/15 | 85,549 | 6,131 | 11.8 | 0.000 | 92.8 |
| codeatlas | 6/15 | 110,708 | 4,989 | 10.4 | 0.000 | 80.6 |

| Versus plain | Tokens | Turns | Cost | Time | Paired tasks |
| --- | ---: | ---: | ---: | ---: | ---: |
| codeatlas | +29% | -12% | - | -13% | 5 |

| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |
| --- | --- | --- | ---: | ---: | ---: |
| find-coupon-expiry | plain | 2/3 | 24,290 | 7.3 | 0.000 |
| find-coupon-expiry | codeatlas | 3/3 | 54,293 | 6.7 | 0.000 |
| impact-payment-processors | codeatlas | 1/3 | 63,999 | 8.0 | 0.000 |
| impact-payment-processors | plain | 1/3 | 46,831 | 7.0 | 0.000 |
| impact-open-orders-callers | plain | 1/3 | 34,073 | 6.3 | 0.000 |
| impact-open-orders-callers | codeatlas | 0/3 | 38,666 | 5.0 | 0.000 |
| change-coupon-overload-rename | codeatlas | 1/3 | 253,479 | 18.3 | 0.000 |
| change-coupon-overload-rename | plain | 0/3 | 90,118 | 14.0 | 0.000 |
| change-charge-currency | plain | 1/3 | 232,432 | 24.3 | 0.000 |
| change-charge-currency | codeatlas | 1/3 | 143,104 | 14.0 | 0.000 |

Answer parsing (find/impact runs): backslashes+json-list 3, backslashes+json-object 2, double-encoded+json-object 5, json-object 4, layout+backslashes+json-object 1, layout+json-object 2, text-fallback 1. Successes that needed a lenient parse: plain 4, codeatlas 2.

Tokens are input + cache creation + cache read + output across all models used by the session.
Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds.
