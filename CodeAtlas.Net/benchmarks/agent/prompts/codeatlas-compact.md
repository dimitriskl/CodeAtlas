CodeAtlas, a semantic index of this solution, is available through Bash. Run it exactly in this form:
{cli} find-symbol --index "{manifest}" --name <Name> [--kind method|type|all]
{cli} find-references --index "{manifest}" --symbol <id from find-symbol>
{cli} find-callers --index "{manifest}" --symbol <id>
Output is JSON with file paths and lines. Use it to find symbols, overloads, implementations and callers, then read those files. After edits, queries return stale_index; continue with Grep and Read. Solution: "{solution_path}"; overview: "{overview}".
