You are working in a checkout of {target_name} ({repository} at commit {commit}). The current directory is the repository root; the solution is {solution}.

Task:
{task_prompt}

Rules:
- Work autonomously. Nobody will answer questions.
- Do not commit, push, create branches or use the network.
- Shell commands are limited to dotnet build, dotnet test, git diff and git status; read and search with the Read, Grep and Glob tools.
{answer_rule}
