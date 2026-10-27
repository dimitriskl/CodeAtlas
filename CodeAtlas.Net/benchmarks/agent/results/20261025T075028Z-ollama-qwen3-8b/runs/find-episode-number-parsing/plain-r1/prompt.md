You are working in a checkout of jellyfin (https://github.com/jellyfin/jellyfin.git at commit e83a7e62f26443f7dd98f126d6955ac1af090125). The current directory is the repository root; the solution is Jellyfin.sln.

Task:
When the library scan meets a file such as 'Show Name/Season 02/Show Name S02E05.mkv', which method applies the episode naming expressions to extract the season and episode numbers from the path? Report the file and the method (Type.Method).

Rules:
- Work autonomously. Nobody will answer questions.
- Do not commit, push, create branches or use the network.
- Shell commands are limited to dotnet build, dotnet test, git diff and git status; read and search with the Read, Grep and Glob tools.
- When you are done, write your answer as JSON to codeatlas-bench-answer.json in the repository root: {"files": [repository-relative paths with forward slashes], "symbols": [Type.Member or type names]}. List only what the task asks for.
