You are working in a checkout of jellyfin (https://github.com/jellyfin/jellyfin.git at commit e83a7e62f26443f7dd98f126d6955ac1af090125). The current directory is the repository root; the solution is Jellyfin.sln.

Task:
Add a CancellationToken parameter named cancellationToken, without a default value, as the last parameter of IUserManager.ChangePassword(Guid userId, string newPassword). Update its implementation so the token is used, and update every caller: pass an available token where one exists, otherwise CancellationToken.None. Do not keep an overload with the old signature. Do not change IAuthenticationProvider.ChangePassword or its implementations. The solution, including tests, must build.

Rules:
- Work autonomously. Nobody will answer questions.
- Do not commit, push, create branches or use the network.
- Shell commands are limited to dotnet build, dotnet test, git diff and git status; read and search with the Read, Grep and Glob tools.
- Leave your change in the working tree. It is checked by building the solution and inspecting the source.
