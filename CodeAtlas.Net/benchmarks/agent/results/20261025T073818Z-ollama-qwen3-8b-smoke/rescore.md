# Rescore: ollama-qwen3-8b-smoke

Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.

| Mode | Runs | Old success | New success | New via lenient parse |
| --- | ---: | ---: | ---: | ---: |
| plain | 1 | 0 | 0 | 0 |

| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| find-password-verification | plain | 1 | False | False | file | json-object | files: {"missing": ["jellyfin.server.implementations/users/defaultauthenticationprovider.cs"], "forbidden": []}; symbols: {"missingGroups": [["DefaultAuthenticationProvider.Authenticate"]]} |
