# Agent benchmark: ollama-qwen3-8b-small-compact

- Target: shop  (`16f89b6822e62675c3be73acb72ee199194dc57e`)
- Agent: Claude Code 2.1.310 (Claude Code), provider ollama, model `qwen3-8b-32k`, 3 repetition(s), started 20261025T083534Z
- Suite: small; agent: compact (short system prompt, minimal tools)
- Ollama 0.40.10 at http://localhost:11434: num_ctx 32768; local model, cost recorded as 0
- CodeAtlas: 0.00.32; upfront index 7.1 s, no model cost
- LSP mode: unavailable: csharp-ls is not installed (dotnet tool install --global csharp-ls); the LSP mode is skipped

| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| plain | 1/15 | 125,103 | 6,528 | 15.2 | 0.000 | 99.9 |
| codeatlas | 4/15 | 94,318 | 6,013 | 9.5 | 0.000 | 100.7 |

| Versus plain | Tokens | Turns | Cost | Time | Paired tasks |
| --- | ---: | ---: | ---: | ---: | ---: |
| codeatlas | -25% | -37% | - | +1% | 5 |

| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |
| --- | --- | --- | ---: | ---: | ---: |
| find-coupon-expiry | plain | 0/3 | 26,265 | 7.7 | 0.000 |
| find-coupon-expiry | codeatlas | 1/3 | 50,163 | 7.0 | 0.000 |
| impact-payment-processors | codeatlas | 0/3 | 98,647 | 9.7 | 0.000 |
| impact-payment-processors | plain | 0/3 | 60,359 | 8.7 | 0.000 |
| impact-open-orders-callers | plain | 0/3 | 150,982 | 16.3 | 0.000 |
| impact-open-orders-callers | codeatlas | 0/3 | 33,765 | 4.0 | 0.000 |
| change-coupon-overload-rename | codeatlas | 2/3 | 127,464 | 12.7 | 0.000 |
| change-coupon-overload-rename | plain | 1/3 | 87,231 | 14.7 | 0.000 |
| change-charge-currency | plain | 0/3 | 300,675 | 28.7 | 0.000 |
| change-charge-currency | codeatlas | 1/3 | 161,551 | 14.3 | 0.000 |

Tokens are input + cache creation + cache read + output across all models used by the session.
Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds.
