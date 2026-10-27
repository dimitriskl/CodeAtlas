You are working in a checkout of jellyfin (https://github.com/jellyfin/jellyfin.git at commit e83a7e62f26443f7dd98f126d6955ac1af090125). The current directory is the repository root; the solution is Jellyfin.sln.

Task:
IUserDataManager has two SaveUserData overloads. Rename only the overload that takes a UserItemData and a CancellationToken to SaveUserItemData, in the interface, its implementation and every call site. Keep the overload that takes an UpdateUserItemDataDto named SaveUserData, and do not keep a forwarding method with the old name. The solution, including tests, must build.

Rules:
- Work autonomously. Nobody will answer questions.
- Do not commit, push, create branches or use the network.
- Shell commands are limited to dotnet build, dotnet test, git diff and git status; read and search with the Read, Grep and Glob tools.
- Leave your change in the working tree. It is checked by building the solution and inspecting the source.
