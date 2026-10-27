# Agent benchmark: ollama-qwen3-8b-small-smoke2

- Target: shop  (`16f89b6822e62675c3be73acb72ee199194dc57e`)
- Agent: Claude Code 2.1.310 (Claude Code), provider ollama, model `qwen3-8b-32k`, 1 repetition(s), started 20261025T083322Z
- Suite: small; agent: compact (short system prompt, minimal tools)
- Ollama 0.40.10 at http://localhost:11434: num_ctx 32768; local model, cost recorded as 0
- CodeAtlas: -; upfront index - s, no model cost
- LSP mode: unavailable: csharp-ls is not installed (dotnet tool install --global csharp-ls); the LSP mode is skipped

| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| plain | 0/2 | 115,071 | 3,982 | 20.0 | 0.000 | 55.5 |

| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |
| --- | --- | --- | ---: | ---: | ---: |
| find-coupon-expiry | plain | 0/1 | 30,443 | 9.0 | 0.000 |
| change-coupon-overload-rename | plain | 0/1 | 199,699 | 31.0 | 0.000 |

Tokens are input + cache creation + cache read + output across all models used by the session.
Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds.
