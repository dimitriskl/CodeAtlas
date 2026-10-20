# Working on CodeAtlas

## Required development location

Development must take place in `C:\code\codeatlas\codeatlas.net`. This is the user-designated working directory. Before doing development in any other folder, machine, or Git worktree, ask the user and wait for explicit approval. Do not silently fall back to the former cloud directory `/workspace/CodeAtlas`. If this Windows directory is unavailable in the current session, explain that limitation and stop development until a local session is attached or the user approves another location. Read-only inspection and preparing a requested transfer are not permission to continue development elsewhere.

CodeAtlas uses our C# library and CLI directly. Do not add an MCP layer.

Read README.md for build commands and current analysis limitations. Use the SDK pinned in global.json and locked NuGet restores for routine setup.

## Versioning every change

For each completed code, configuration, or documentation change, increment the version once with `python3 scripts/bump-version.py --summary "Describe the completed change"`. The canonical version is in Version.props; the command also updates README.md, docs/CodeAtlas-Project-Recap.md, and docs/VERSION.md. Keep earlier history entries intact. Multiple file edits and fixes made while validating the same change are one version increment, not separate increments.

Follow `x.xx.xx`: increment the last two digits; `0.00.99` becomes `0.01.00`. Keep the major component at 0 until the user explicitly authorizes publishing CodeAtlas itself. Cloud environment publication is not a major product release. Do not automatically roll `0.99.99` into a major release. Read docs/VERSION.md for the full policy.

After incrementing, rebuild so `--version` matches Version.props, run the .NET tests and `python3 -m unittest discover -s tests/versioning -p 'test_*.py'`, then regenerate the index. Version fields in generated artifacts are output; do not edit them by hand.

## Codebase navigation

For codebase navigation, use the solution-scoped `current.json` manifest returned as `index` by the latest index command. Locate it under `.codeatlas/solutions/<solution-file>-<absolute-path-hash>/` when available:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status --index <solution-current.json>
```

If the index is missing or stale, build CodeAtlas and regenerate it:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- index --solution CodeAtlas.slnx
```

Read the immutable `overview` path returned by the index command for orientation. Keep its generation together with the returned `warningsFile` and `timings` paths. Use `find-symbol --index <solution-current.json> --name <method>` and `find-references --index <solution-current.json> --symbol <id>` through the same CLI to inspect relevant relationships. Read the source before editing; static references are not a complete runtime call graph.

After source changes, run `dotnet run --project src/CodeAtlas.Cli --no-build -- refresh --solution CodeAtlas.slnx` before querying again. Refresh safely falls back to full analysis when reuse cannot be established; `index` always requests full analysis. Use the new snapshot ID for continuation requests. Keep `.codeatlas/` outputs out of Git. Validate changes with the documented build and test commands.
