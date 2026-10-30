import { compileCSharpExe } from "./compile-csharp.js";

const windowsKeychainProgram = [
  "using System;",
  "using System.IO;",
  "class FakePowerShell {",
  "  static int Main(string[] args) {",
  '    string dir = Environment.GetEnvironmentVariable("CODEATLAS_FAKE_KEYCHAIN_DIR");',
  "    if (string.IsNullOrEmpty(dir)) return 1;",
  "    Directory.CreateDirectory(dir);",
  '    string operation = "";',
  '    string target = "";',
  "    for (int i = 0; i < args.Length; i++) {",
  '      if (args[i] == "read" || args[i] == "write") {',
  "        operation = args[i];",
  "        if (i + 1 < args.Length) target = args[i + 1];",
  "      }",
  "    }",
  '    string provider = target.StartsWith("CodeAtlas:ai:") ? target.Substring("CodeAtlas:ai:".Length) : "";',
  '    if (operation == "read") {',
  '      string stored = Path.Combine(dir, "value-" + provider);',
  "      if (File.Exists(stored)) {",
  "        Console.Out.Write(File.ReadAllText(stored));",
  "        return 0;",
  "      }",
  "      return 1168;",
  "    }",
  "    string stdin;",
  "    using (StreamReader reader = new StreamReader(Console.OpenStandardInput())) {",
  "      stdin = reader.ReadToEnd();",
  "    }",
  '    File.WriteAllText(Path.Combine(dir, "stdin"), stdin);',
  '    File.WriteAllText(Path.Combine(dir, "args"), string.Join("\\n", args) + "\\n");',
  '    if (operation == "write") File.WriteAllText(Path.Combine(dir, "value-" + provider), stdin);',
  "    return 0;",
  "  }",
  "}",
  "",
].join("\n");

/** A Credential Manager stand-in at the absolute powershell.exe path. Windows only. */
export function installFakeWindowsPowerShell(file: string): void {
  compileCSharpExe(file, windowsKeychainProgram);
}
