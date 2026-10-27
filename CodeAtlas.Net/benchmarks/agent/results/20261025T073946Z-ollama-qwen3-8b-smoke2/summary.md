# Agent benchmark: ollama-qwen3-8b-smoke2

- Target: jellyfin v10.11.9 (`e83a7e62f26443f7dd98f126d6955ac1af090125`)
- Agent: Claude Code 2.1.310 (Claude Code), provider ollama, model `qwen3-8b-32k`, 1 repetition(s), started 20261025T073946Z
- Ollama 0.40.10 at http://localhost:11434: num_ctx 32768; local model, cost recorded as 0
- CodeAtlas: 0.00.31; upfront index 308.7 s, no model cost
- LSP mode: unavailable: csharp-ls is not installed (dotnet tool install --global csharp-ls); the LSP mode is skipped

| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| codeatlas | 0/1 | 69,403 | 4,372 | 8.0 | 0.000 | 65.7 |

| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |
| --- | --- | --- | ---: | ---: | ---: |
| find-password-verification | codeatlas | 0/1 | 69,403 | 8.0 | 0.000 |

Tokens are input + cache creation + cache read + output across all models used by the session.
Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds.
