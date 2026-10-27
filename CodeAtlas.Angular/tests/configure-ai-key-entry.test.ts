import { spawnSync } from "node:child_process";
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Readable, Writable } from "node:stream";
import { describe, expect, it } from "vitest";
import { runConfigureAi, type SecretInput } from "../src/configure-ai.js";
import type { Keychain } from "../src/keychain.js";
import { runCli } from "./support/run-cli.js";

const endpoint = "http://127.0.0.1:9/v1/chat/completions";
const secret = "sk-synthetic-credential-0123456789";
const prompt = "Enter the API key, or set CODEATLAS_AI_OPENAI_KEY (hidden; Esc cancels): ";

function isolatedEnv(root: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value === undefined) continue;
    if (key.startsWith("CODEATLAS_")) continue;
    if (key.endsWith("_API_KEY") || key.endsWith("_APIKEY")) continue;
    if (/OPENAI|ANTHROPIC|AZURE|MOONSHOT|OPENROUTER|KIMI|OLLAMA/i.test(key)) continue;
    env[key] = value;
  }
  delete env.DBUS_SESSION_BUS_ADDRESS;
  env.XDG_CONFIG_HOME = path.join(root, "xdg");
  env.APPDATA = path.join(root, "appdata");
  env.HOME = path.join(root, "home");
  env.USERPROFILE = path.join(root, "home");
  return env;
}

function withoutKeychain(env: NodeJS.ProcessEnv, root: string): void {
  env.PATH = path.join(root, "empty-path");
  env.Path = env.PATH;
  mkdirSync(env.PATH, { recursive: true });
  delete env.DBUS_SESSION_BUS_ADDRESS;
}

function installFakeKeychain(root: string, env: NodeJS.ProcessEnv): string {
  const bin = path.join(root, "fake-bin");
  const state = path.join(root, "fake-keychain");
  mkdirSync(bin, { recursive: true });
  mkdirSync(state, { recursive: true });
  env.CODEATLAS_FAKE_KEYCHAIN_DIR = state;
  env.DBUS_SESSION_BUS_ADDRESS = "unix:path=/tmp/codeatlas-fake-bus";
  if (process.platform === "win32") {
    writeFileSync(
      path.join(bin, "powershell.js"),
      [
        "const fs = require('node:fs');",
        "const path = require('node:path');",
        "const dir = process.env.CODEATLAS_FAKE_KEYCHAIN_DIR;",
        "fs.mkdirSync(dir, { recursive: true });",
        "const args = process.argv.slice(2);",
        "const operation = args.find((arg) => arg === 'read' || arg === 'write') ?? '';",
        "if (operation === 'read') process.exit(1168);",
        "fs.writeFileSync(path.join(dir, 'stdin'), fs.readFileSync(0));",
        "fs.writeFileSync(path.join(dir, 'args'), args.join('\\n') + '\\n');",
        "process.exit(0);",
        "",
      ].join("\n"),
    );
  } else {
    const tool = path.join(bin, "secret-tool");
    writeFileSync(
      tool,
      [
        "#!/bin/sh",
        "set -eu",
        'dir="$CODEATLAS_FAKE_KEYCHAIN_DIR"',
        'mkdir -p "$dir"',
        'op="${1:-}"',
        'if [ "$op" = "lookup" ]; then exit 1; fi',
        'printf "%s\\n" "$@" > "$dir/args"',
        'cat > "$dir/stdin"',
        "exit 0",
        "",
      ].join("\n"),
    );
    chmodSync(tool, 0o755);
  }
  env.PATH = bin;
  env.Path = bin;
  return state;
}

function scriptedTty(text: string): SecretInput {
  let pending: string | null = text;
  const stream = new Readable({
    read() {
      if (pending === null) return;
      const next = pending;
      pending = null;
      this.push(next);
    },
  });
  const tty = stream as SecretInput;
  tty.isTTY = true;
  tty.setRawMode = () => undefined;
  return tty;
}

function captureStderr(): { stderr: Writable; text: () => string } {
  let captured = "";
  const stderr = new Writable({
    write(chunk: Buffer | string, encoding, callback) {
      void encoding;
      captured += typeof chunk === "string" ? chunk : chunk.toString("utf8");
      callback();
    },
  });
  return { stderr, text: () => captured };
}

function unavailableKeychain(): Keychain {
  return {
    name: "OS keychain",
    isAvailable: () => false,
    read: () => null,
    write: () => {
      throw new Error("A key was written with no keychain.");
    },
  };
}

function recordingKeychain(): { keychain: Keychain; writes: string[] } {
  const writes: string[] = [];
  return {
    writes,
    keychain: {
      name: "Linux Secret Service",
      isAvailable: () => true,
      read: () => null,
      write: (_provider, value) => {
        writes.push(value);
      },
    },
  };
}

function providerFetch(): { fetchImpl: typeof fetch; authorizations: string[] } {
  const authorizations: string[] = [];
  const fetchImpl: typeof fetch = (input, init) => {
    void input;
    const headers = new Headers(init?.headers);
    authorizations.push(headers.get("authorization") ?? "");
    return Promise.resolve(
      new Response(
        JSON.stringify({
          choices: [{ finish_reason: "stop", message: { content: '{"ok":true}' } }],
        }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );
  };
  return { fetchImpl, authorizations };
}

const configureArgs = ["--provider", "openai", "--model", "gpt-x", "--endpoint", endpoint] as const;

describe("configure-ai key entry", () => {
  it("saves settings and does not prompt when no keychain and stdin is closed", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    withoutKeychain(env, root);
    mkdirSync(env.XDG_CONFIG_HOME ?? root, { recursive: true });

    // Act
    const result = await runCli(["configure-ai", ...configureArgs], {
      cwd: root,
      env,
      timeoutMs: 2_000,
    });

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    const error = JSON.parse(result.stderr) as { error: { code: string; message: string } };
    expect(error.error.code).toBe("ai_configuration_invalid");
    expect(error.error.message).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect(error.error.message).not.toContain("interactive terminal");
    expect(result.stderr).not.toContain("Enter the API key");
    expect(
      JSON.parse(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")),
    ).toMatchObject({
      ai: { provider: "openai", model: "gpt-x", endpoint },
    });
  });

  it("saves settings and does not wait when no keychain and stdin stays open", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    withoutKeychain(env, root);

    // Act
    const result = await runCli(["configure-ai", ...configureArgs], {
      cwd: root,
      env,
      holdStdin: true,
      timeoutMs: 2_000,
    });

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stderr).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect(result.stderr).not.toContain("interactive terminal");
    expect(result.stderr).not.toContain("Enter the API key");
    expect(
      JSON.parse(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")),
    ).toMatchObject({
      ai: { provider: "openai", model: "gpt-x", endpoint },
    });
  });

  it("saves settings and does not prompt when no keychain and stdin is a TTY", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const { stderr, text } = captureStderr();
    const tty = scriptedTty(`${secret}\r`);

    // Act
    const failure = await runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty,
      stderr,
      keychain: unavailableKeychain(),
      fetchImpl: () => Promise.reject(new Error("connection was attempted")),
    }).then(
      () => null,
      (error: unknown) => error,
    );

    // Assert
    expect(failure).toBeInstanceOf(Error);
    expect((failure as Error).message).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect((failure as Error).message).not.toContain("interactive terminal");
    expect(text()).toBe("");
    expect(text()).not.toContain(secret);
    expect(
      JSON.parse(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")),
    ).toMatchObject({
      ai: { provider: "openai", model: "gpt-x", endpoint },
    });
  });

  it("prompts on a TTY without echo and stores the typed key", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const { stderr, text } = captureStderr();
    const tty = scriptedTty(`${secret}X\u007f\r`);
    const { keychain, writes } = recordingKeychain();
    const { fetchImpl, authorizations } = providerFetch();

    // Act
    const output = await runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty,
      stderr,
      keychain,
      fetchImpl,
    });

    // Assert
    expect(output.success).toBe(true);
    expect(output.credentialStore).toBe("Linux Secret Service");
    expect(writes).toEqual([secret]);
    expect(authorizations).toEqual([`Bearer ${secret}`]);
    expect(text()).toBe(`${prompt}\n`);
    expect(text()).not.toContain(secret);
    expect(JSON.stringify(output)).not.toContain(secret);
    expect(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")).not.toContain(
      secret,
    );
  });

  it("caps a TTY key at 8192 characters", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const { stderr, text } = captureStderr();
    const capped = "a".repeat(8192);
    const tty = scriptedTty(`${capped}Z\r`);
    const { keychain, writes } = recordingKeychain();
    const { fetchImpl } = providerFetch();

    // Act
    await runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty,
      stderr,
      keychain,
      fetchImpl,
    });

    // Assert
    expect(writes).toEqual([capped]);
    expect(text()).toBe(`${prompt}\n`);
    expect(text()).not.toContain(capped);
    expect(text()).not.toContain("Z");
  });

  it("cancels a TTY prompt on Esc without the interactive-terminal instruction", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const { stderr, text } = captureStderr();
    const tty = scriptedTty(`sk-\u001b`);
    const { keychain, writes } = recordingKeychain();

    // Act
    const failure = await runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty,
      stderr,
      keychain,
      fetchImpl: () => Promise.reject(new Error("connection was attempted")),
    }).then(
      () => null,
      (error: unknown) => error,
    );

    // Assert
    expect((failure as Error).message).toBe("An API key is required. Set CODEATLAS_AI_OPENAI_KEY.");
    expect((failure as Error).message).not.toContain("interactive terminal");
    expect(writes).toEqual([]);
    expect(text()).toBe(`${prompt}\n`);
    expect(text()).not.toContain("sk-");
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
  });

  it.skipIf(process.platform === "darwin")(
    "fails immediately when a keychain exists and an open pipe has no key",
    async () => {
      // Arrange
      const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
      const env = isolatedEnv(root);
      const state = installFakeKeychain(root, env);

      // Act
      const result = await runCli(["configure-ai", ...configureArgs], {
        cwd: root,
        env,
        holdStdin: true,
        timeoutMs: 2_000,
      });

      // Assert
      expect(result.exitCode).toBe(1);
      expect(result.stdout).toBe("");
      expect(JSON.parse(result.stderr)).toMatchObject({
        error: {
          message:
            "An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_OPENAI_KEY.",
        },
      });
      expect(result.stderr).not.toContain("Enter the API key");
      expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
      expect(existsSync(path.join(state, "stdin"))).toBe(false);
    },
  );

  it.skipIf(process.platform === "darwin")(
    "fails immediately when a keychain exists and stdin is closed",
    async () => {
      // Arrange
      const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
      const env = isolatedEnv(root);
      installFakeKeychain(root, env);

      // Act
      const result = await runCli(["configure-ai", ...configureArgs], {
        cwd: root,
        env,
        timeoutMs: 2_000,
      });

      // Assert
      expect(result.exitCode).toBe(1);
      expect(result.stdout).toBe("");
      expect(JSON.parse(result.stderr)).toMatchObject({
        error: {
          message:
            "An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_OPENAI_KEY.",
        },
      });
      expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
    },
  );

  it("reads the Windows user-scope key with System32 reg.exe and ignores a workspace reg", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-reg-"));
    const env = isolatedEnv(root);
    const systemRoot = path.join(root, "winroot");
    const systemReg = path.join(systemRoot, "System32", "reg.exe");
    const systemMarker = path.join(root, "system-reg.txt");
    const decoyMarker = path.join(root, "decoy-reg.txt");
    installReg(systemReg, systemMarker, false);
    installReg(path.join(root, "reg"), decoyMarker, true);
    installReg(path.join(root, "reg.exe"), decoyMarker, true);
    const decoyBin = path.join(root, "decoy-bin");
    mkdirSync(decoyBin, { recursive: true });
    installReg(path.join(decoyBin, "reg"), decoyMarker, true);
    installReg(path.join(decoyBin, "reg.exe"), decoyMarker, true);
    env.SystemRoot = systemRoot;
    env.PATH = decoyBin;
    env.Path = decoyBin;
    env.CODEATLAS_REG_MARKER = systemMarker;
    env.CODEATLAS_DECOY_MARKER = decoyMarker;
    const { fetchImpl, authorizations } = providerFetch();

    // Act
    const output = await runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "win32",
      stdin: scriptedTty(`${secret}\r`),
      stderr: captureStderr().stderr,
      keychain: unavailableKeychain(),
      fetchImpl,
    });

    // Assert
    expect(output.credentialStore).toBe("environment variable CODEATLAS_AI_OPENAI_KEY");
    expect(authorizations).toEqual(["Bearer user-scope-key"]);
    expect(existsSync(decoyMarker)).toBe(false);
    const recorded = readFileSync(systemMarker, "utf8");
    expect(recorded).toContain(systemReg);
    expect(recorded).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect(recorded).not.toContain(secret);
    expect(JSON.stringify(output)).not.toContain("user-scope-key");
  });
});

function installReg(file: string, marker: string, decoy: boolean): void {
  mkdirSync(path.dirname(file), { recursive: true });
  if (process.platform === "win32" && file.endsWith(".exe")) {
    compileWindowsReg(file, marker, decoy);
    return;
  }
  const body = decoy
    ? "#!/bin/sh\nprintf '%s\\n' decoy > \"$CODEATLAS_DECOY_MARKER\"\nexit 99\n"
    : [
        "#!/bin/sh",
        'printf "%s\\n" "$0" > "$CODEATLAS_REG_MARKER"',
        'printf "%s\\n" "$@" >> "$CODEATLAS_REG_MARKER"',
        'name=""',
        'prev=""',
        'for arg in "$@"; do',
        '  if [ "$prev" = "/v" ]; then name="$arg"; fi',
        '  prev="$arg"',
        "done",
        "printf '\\nHKEY_CURRENT_USER\\\\Environment\\n    %s    REG_SZ    user-scope-key\\n' \"$name\"",
        "exit 0",
        "",
      ].join("\n");
  writeFileSync(file, body);
  chmodSync(file, 0o755);
}

function compileWindowsReg(file: string, marker: string, decoy: boolean): void {
  const dir = path.dirname(file);
  const source = path.join(dir, `${path.basename(file)}.cs`);
  const markerVariable = decoy ? "CODEATLAS_DECOY_MARKER" : "CODEATLAS_REG_MARKER";
  const body = decoy
    ? `File.WriteAllText(Environment.GetEnvironmentVariable("${markerVariable}"), "decoy"); return 99;`
    : [
        `var markerPath = Environment.GetEnvironmentVariable("${markerVariable}");`,
        `var recorded = Environment.GetCommandLineArgs()[0] + "\\n" + string.Join("\\n", args);`,
        `File.WriteAllText(markerPath, recorded);`,
        `string name = "";`,
        `for (int i = 0; i < args.Length - 1; i++) if (args[i] == "/v") name = args[i + 1];`,
        `Console.WriteLine();`,
        `Console.WriteLine("HKEY_CURRENT_USER\\\\Environment");`,
        `Console.WriteLine("    " + name + "    REG_SZ    user-scope-key");`,
        `return 0;`,
      ].join("\n");
  writeFileSync(
    source,
    [
      "using System;",
      "using System.IO;",
      "class FakeReg {",
      "  static int Main(string[] args) {",
      body,
      "  }",
      "}",
      "",
    ].join("\n"),
  );
  const roots = [process.env.SystemRoot, process.env.WINDIR, "C:\\Windows"].filter(
    (value): value is string => typeof value === "string" && value.length > 0,
  );
  const compilers = roots.flatMap((root) => [
    path.join(root, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
    path.join(root, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
  ]);
  for (const csc of compilers) {
    if (!existsSync(csc)) continue;
    const compiled = spawnSync(csc, ["/nologo", `/out:${file}`, source], { encoding: "utf8" });
    if (compiled.status === 0) return;
  }
  const powershell = spawnSync(
    "powershell.exe",
    [
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-Command",
      `Add-Type -OutputType ConsoleApplication -OutputAssembly '${file}' -TypeDefinition (Get-Content -Raw '${source}')`,
    ],
    { encoding: "utf8" },
  );
  if (powershell.status === 0 && existsSync(file)) return;
  throw new Error(`Could not compile ${file} (marker ${marker}).`);
}
