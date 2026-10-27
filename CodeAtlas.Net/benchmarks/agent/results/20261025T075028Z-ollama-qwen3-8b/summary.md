# Agent benchmark: ollama-qwen3-8b

- Target: jellyfin v10.11.9 (`e83a7e62f26443f7dd98f126d6955ac1af090125`)
- Agent: Claude Code 2.1.310 (Claude Code), provider ollama, model `qwen3-8b-32k`, 1 repetition(s), started 20261025T075028Z
- Ollama 0.40.10 at http://localhost:11434: num_ctx 32768; local model, cost recorded as 0
- CodeAtlas: 0.00.31; upfront index 308.7 s, no model cost
- LSP mode: unavailable: csharp-ls is not installed (dotnet tool install --global csharp-ls); the LSP mode is skipped

| Mode | Success | Mean tokens | Mean output | Mean turns | Mean cost USD | Mean time s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| plain | 0/5 | 297,402 | 5,984 | 15.2 | 0.000 | 123.0 |
| codeatlas | 0/6 | 337,278 | 7,546 | 16.0 | 0.000 | 154.9 |

| Versus plain | Tokens | Turns | Cost | Time | Paired tasks |
| --- | ---: | ---: | ---: | ---: | ---: |
| codeatlas | -3% | -13% | - | -0% | 5 |

| Task | Mode | Success | Mean tokens | Mean turns | Mean cost USD |
| --- | --- | --- | ---: | ---: | ---: |
| find-password-verification | plain | 0/1 | 50,755 | 7.0 | 0.000 |
| find-password-verification | codeatlas | 0/1 | 93,262 | 8.0 | 0.000 |
| find-episode-number-parsing | codeatlas | 0/1 | 11,390 | 2.0 | 0.000 |
| find-episode-number-parsing | plain | 0/1 | 59,114 | 9.0 | 0.000 |
| impact-metadata-saver-implementations | plain | 0/1 | 44,941 | 4.0 | 0.000 |
| impact-metadata-saver-implementations | codeatlas | 0/1 | 22,542 | 3.0 | 0.000 |
| impact-static-media-sources | codeatlas | 0/1 | 12,464 | 2.0 | 0.000 |
| impact-static-media-sources | plain | 0/1 | 71,035 | 5.0 | 0.000 |
| change-changepassword-cancellation | plain | 0/1 | 1,261,166 | 51.0 | 0.000 |
| change-changepassword-cancellation | codeatlas | 0/1 | 1,303,434 | 51.0 | 0.000 |
| change-saveuserdata-overload-rename | codeatlas | 0/1 | 580,575 | 30.0 | 0.000 |

Tokens are input + cache creation + cache read + output across all models used by the session.
Time is wall time of the agent process; CodeAtlas refreshes between runs are reported separately as setupSeconds.

**Stopped:** Stopped on user request during change-saveuserdata-overload-rename/plain to switch to the small suite; 11 of 12 sessions completed, the interrupted session was not recorded.
