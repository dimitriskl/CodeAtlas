You are working in a checkout of jellyfin (https://github.com/jellyfin/jellyfin.git at commit e83a7e62f26443f7dd98f126d6955ac1af090125). The current directory is the repository root; the solution is Jellyfin.sln.

Task:
IUserDataManager has two SaveUserData overloads. Rename only the overload that takes a UserItemData and a CancellationToken to SaveUserItemData, in the interface, its implementation and every call site. Keep the overload that takes an UpdateUserItemDataDto named SaveUserData, and do not keep a forwarding method with the old name. The solution, including tests, must build.

Rules:
- Work autonomously. Nobody will answer questions.
- Do not commit, push, create branches or use the network.
- Shell commands are limited to dotnet build, dotnet test, git diff and git status; read and search with the Read, Grep and Glob tools.
- Leave your change in the working tree. It is checked by building the solution and inspecting the source.

---
CodeAtlas, a Roslyn semantic index of this solution, is available as a CLI. The index is fresh when the task starts.

CLI (run it exactly in this form; the shell allows this prefix): C:/Users/photd/AppData/Local/Programs/Python/Python315/python.exe C:/temp/codeatlas-bench/tools/codeatlas.py
Index manifest: "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json"
Overview (projects and types): "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/generations/090d78e7740a4dcdaf15dd72e2909e7a/overview.md"

Commands print JSON on stdout:
- find-symbol --index "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json" --name <MethodName> [--type <DeclaringType>] [--kind method|type|property|field|all]
- find-references --index "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json" --symbol <id returned by find-symbol>
- find-callers --index "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json" --symbol <id> [--depth 1-5]
- search-symbols --index "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json" --query <words>
- get-facts --index "C:/temp/codeatlas-bench/work/codeatlas/.codeatlas/solutions/Jellyfin.sln-C8B0AE2780728F4EE537D093E91FDC8FD6F8E3CB18ADC5E3B79B9AC70ED67442/current.json" --symbol <id>   (interfaces, resolved calls and other Roslyn facts)

Use CodeAtlas to locate symbols, overloads, implementations and references before reading or editing source, then read the source at the returned locations. Method IDs distinguish overloads; references include interface and override relationships but not reflection or DI. After you edit files, indexed queries refuse with stale_index (exit code 3); continue with ordinary tools or run C:/Users/photd/AppData/Local/Programs/Python/Python315/python.exe C:/temp/codeatlas-bench/tools/codeatlas.py refresh --solution "C:/temp/codeatlas-bench/work/codeatlas/Jellyfin.sln" (about a minute).