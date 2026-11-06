import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";

/** Compile a tiny console exe with the Windows Framework C# compiler. */
export function compileCSharpExe(file: string, source: string): void {
  const dir = path.dirname(file);
  mkdirSync(dir, { recursive: true });
  const sourceFile = path.join(dir, `${path.basename(file)}.cs`);
  writeFileSync(sourceFile, source);
  const roots = [process.env.SystemRoot, process.env.WINDIR, "C:\\Windows"].filter(
    (value): value is string => typeof value === "string" && value.length > 0,
  );
  const compilers = roots.flatMap((root) => [
    path.join(root, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
    path.join(root, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
  ]);
  for (const csc of compilers) {
    if (!existsSync(csc)) continue;
    const compiled = spawnSync(csc, ["/nologo", `/out:${file}`, sourceFile], { encoding: "utf8" });
    if (compiled.status === 0 && existsSync(file)) return;
  }
  const powershell = spawnSync(
    "powershell.exe",
    [
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-Command",
      `Add-Type -OutputType ConsoleApplication -OutputAssembly '${file.replace(/'/g, "''")}' -TypeDefinition (Get-Content -Raw '${sourceFile.replace(/'/g, "''")}')`,
    ],
    { encoding: "utf8" },
  );
  if (powershell.status === 0 && existsSync(file)) return;
  throw new Error(`Could not compile ${file}.`);
}
