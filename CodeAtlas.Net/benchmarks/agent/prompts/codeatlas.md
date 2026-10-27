CodeAtlas, a Roslyn semantic index of this solution, is available as a CLI. The index is fresh when the task starts.

CLI (run it exactly in this form; the shell allows this prefix): {cli}
Index manifest: "{manifest}"
Overview (projects and types): "{overview}"

Commands print JSON on stdout:
- find-symbol --index "{manifest}" --name <MethodName> [--type <DeclaringType>] [--kind method|type|property|field|all]
- find-references --index "{manifest}" --symbol <id returned by find-symbol>
- find-callers --index "{manifest}" --symbol <id> [--depth 1-5]
- search-symbols --index "{manifest}" --query <words>
- get-facts --index "{manifest}" --symbol <id>   (interfaces, resolved calls and other Roslyn facts)

Use CodeAtlas to locate symbols, overloads, implementations and references before reading or editing source, then read the source at the returned locations. Method IDs distinguish overloads; references include interface and override relationships but not reflection or DI. After you edit files, indexed queries refuse with stale_index (exit code 3); continue with ordinary tools or run {cli} refresh --solution "{solution_path}" (about a minute).
