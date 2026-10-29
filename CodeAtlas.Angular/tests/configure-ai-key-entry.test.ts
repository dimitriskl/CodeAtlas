import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import path from "node:path";
import { Readable, Writable } from "node:stream";
import { describe, expect, it } from "vitest";
import { runConfigureAi, type SecretInput } from "../src/configure-ai.js";
import { createKeychain, windowsPowershellExecutable, type Keychain } from "../src/keychain.js";
import { createProcessRunner } from "../src/process-runner.js";
import { compileCSharpExe } from "./support/compile-csharp.js";
import { runCli, packageRoot } from "./support/run-cli.js";
import { installFakeWindowsPowerShell } from "./support/windows-keychain-tool.js";

const endpoint = "http://127.0.0.1:9/v1/chat/completions";
const secret = "sk-synthetic-credential-0123456789";
const prompt = "Enter the API key, or set CODEATLAS_AI_OPENAI_KEY (hidden; Esc cancels): ";
const scriptProgram = existsSync("/usr/bin/script") ? "/usr/bin/script" : "script";
const ptyHelp = spawnSync(scriptProgram, ["--help"], { encoding: "utf8" });
const ptyAvailable =
  ptyHelp.status === 0 && `${ptyHelp.stdout ?? ""}${ptyHelp.stderr ?? ""}`.includes("--echo");

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
  const systemRoot = path.join(root, "empty-winroot");
  mkdirSync(systemRoot, { recursive: true });
  env.SystemRoot = systemRoot;
  env.SYSTEMROOT = systemRoot;
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
    const systemRoot = path.join(root, "winroot");
    env.SystemRoot = systemRoot;
    env.SYSTEMROOT = systemRoot;
    installFakeWindowsPowerShell(
      path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
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
  const currentPath = env.PATH ?? "";
  env.PATH = currentPath.length === 0 ? bin : `${bin}${path.delimiter}${currentPath}`;
  env.Path = env.PATH;
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

  it("reads a slow pipe through EOF when no keychain is available", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    withoutKeychain(env, root);
    const provider = await startOkProvider();
    const slowEndpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "gpt-x", "--endpoint", slowEndpoint],
        {
          cwd: root,
          env,
          stdinWrites: [
            { delayMs: 0, text: secret.slice(0, 8) },
            { delayMs: 200, text: `${secret.slice(8)}\n`, end: true },
          ],
          timeoutMs: 5_000,
        },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(1);
    expect(provider.authorizations).toEqual([`Bearer ${secret}`]);
    expect(result.stderr).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect(result.stderr).not.toContain("interactive terminal");
    expect(result.stderr).not.toContain(secret);
    expect(result.stderr).not.toContain("Enter the API key");
    expect(
      JSON.parse(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")),
    ).toMatchObject({
      ai: { provider: "openai", model: "gpt-x", endpoint: slowEndpoint },
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
    "reads a slow pipe through EOF and stores the whole key",
    async () => {
      // Arrange
      const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
      const env = isolatedEnv(root);
      const state = installFakeKeychain(root, env);
      const provider = await startOkProvider();
      const slowEndpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
      let result: Awaited<ReturnType<typeof runCli>>;
      try {
        // Act
        result = await runCli(
          ["configure-ai", "--provider", "openai", "--model", "gpt-x", "--endpoint", slowEndpoint],
          {
            cwd: root,
            env,
            stdinWrites: [
              { delayMs: 0, text: secret.slice(0, 8) },
              { delayMs: 200, text: `${secret.slice(8)}\n`, end: true },
            ],
            timeoutMs: 5_000,
          },
        );
      } finally {
        await provider.close();
      }

      // Assert
      expect(result.exitCode).toBe(0);
      expect(result.stdout).not.toContain(secret);
      expect(readFileSync(path.join(state, "stdin"), "utf8")).toBe(secret);
      expect(readFileSync(path.join(state, "args"), "utf8")).not.toContain(secret);
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

  it("keeps stdin referenced while the hidden prompt is open", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const tty = controllableTty();
    const { stderr, text } = captureStderr();
    const { keychain, writes } = recordingKeychain();
    const { fetchImpl, authorizations } = providerFetch();

    // Act
    const pending = runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty.stdin,
      stderr,
      keychain,
      fetchImpl,
    });
    const waiting = tty.refs() > 0 && tty.raw();
    tty.send(`${secret}\r`);
    const output = await pending;

    // Assert
    expect(waiting).toBe(true);
    expect(text()).toBe(`${prompt}\n`);
    expect(text()).not.toContain(secret);
    expect(tty.raw()).toBe(false);
    expect(tty.paused()).toBe(true);
    expect(tty.unrefs()).toBeGreaterThan(1);
    expect(output.success).toBe(true);
    expect(writes).toEqual([secret]);
    expect(authorizations).toEqual([`Bearer ${secret}`]);
  });

  it("cancels a TTY prompt on Ctrl+C without the interactive-terminal instruction", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const tty = controllableTty();
    const { stderr, text } = captureStderr();
    const { keychain, writes } = recordingKeychain();

    // Act
    const pending = runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty.stdin,
      stderr,
      keychain,
      fetchImpl: () => Promise.reject(new Error("connection was attempted")),
    });
    tty.send("\u0003");
    const failure = await pending.then(
      () => null,
      (error: unknown) => error,
    );

    // Assert
    expect((failure as Error).message).toBe("An API key is required. Set CODEATLAS_AI_OPENAI_KEY.");
    expect((failure as Error).message).not.toContain("interactive terminal");
    expect(writes).toEqual([]);
    expect(text()).toBe(`${prompt}\n`);
    expect(tty.raw()).toBe(false);
    expect(tty.paused()).toBe(true);
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
  });

  it("cancels a TTY prompt when stdin closes", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-key-entry-"));
    const env = isolatedEnv(root);
    const tty = controllableTty();
    const { stderr, text } = captureStderr();
    const { keychain, writes } = recordingKeychain();

    // Act
    const pending = runConfigureAi([...configureArgs], {
      cwd: root,
      env,
      platform: "linux",
      stdin: tty.stdin,
      stderr,
      keychain,
      fetchImpl: () => Promise.reject(new Error("connection was attempted")),
    });
    tty.send(null);
    const failure = await pending.then(
      () => null,
      (error: unknown) => error,
    );

    // Assert
    expect((failure as Error).message).toBe("An API key is required. Set CODEATLAS_AI_OPENAI_KEY.");
    expect((failure as Error).message).not.toContain("interactive terminal");
    expect(writes).toEqual([]);
    expect(text()).toBe(`${prompt}\n`);
    expect(tty.raw()).toBe(false);
    expect(tty.paused()).toBe(true);
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
  });

  it.skipIf(process.platform === "darwin" || !ptyAvailable)(
    "waits on a real terminal, accepts a hidden key, and finishes",
    async () => {
      // Arrange
      const root = mkdtempSync(path.join(tmpdir(), "codeatlas-pty-"));
      const env = isolatedEnv(root);
      const state = installFakeKeychain(root, env);
      const provider = await startOkProvider();
      const ptyEndpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
      const cli = path.join(packageRoot, "dist", "cli.js");
      const command = [
        shQuote(process.execPath),
        shQuote(cli),
        "configure-ai",
        "--provider",
        "openai",
        "--model",
        "gpt-x",
        "--endpoint",
        shQuote(ptyEndpoint),
      ].join(" ");
      const typescript = path.join(root, "typescript");
      let output = "";
      let result: { code: number | null; output: string };
      try {
        const child = spawn(
          scriptProgram,
          ["-q", "-e", "-f", "--echo", "never", "-c", command, typescript],
          { cwd: root, env, stdio: ["pipe", "pipe", "pipe"] },
        );
        child.on("error", () => undefined);
        child.stdout.on("data", (chunk: Buffer) => {
          output += chunk.toString("utf8");
        });
        child.stderr.on("data", (chunk: Buffer) => {
          output += chunk.toString("utf8");
        });
        const sawPrompt = await waitForText(() => output, "Enter the API key", 5_000);
        await delay(300);
        const stillWaiting = child.exitCode === null && !child.killed;
        child.stdin.write(`${secret}\r`);
        const code = await waitForClose(child, 10_000);
        result = { code, output: `${output}\n${stillWaiting && sawPrompt ? "still-waiting" : ""}` };
      } finally {
        await provider.close();
      }

      // Assert
      expect(result.output).toContain("still-waiting");
      expect(result.code).toBe(0);
      expect(result.output).not.toContain(secret);
      expect(readFileSync(path.join(state, "stdin"), "utf8")).toBe(secret);
    },
  );

  it("runs Windows Credential Manager at System32 powershell.exe and ignores a PATH powershell", () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-ps-"));
    const env = isolatedEnv(root);
    const systemRoot = path.join(root, "winroot");
    const executable = windowsPowershellExecutable({ SystemRoot: systemRoot });
    const systemMarker = path.join(root, "system-ps.txt");
    const decoyMarker = path.join(root, "decoy-ps.txt");
    const stdinFile = path.join(root, "ps-stdin.txt");
    const decoyBin = path.join(root, "decoy-bin");
    mkdirSync(decoyBin, { recursive: true });
    installPowerShellSpy(executable ?? "", false);
    installPowerShellSpy(path.join(decoyBin, "powershell"), true);
    installPowerShellSpy(path.join(decoyBin, "powershell.exe"), true);
    installPowerShellSpy(path.join(root, "powershell"), true);
    installPowerShellSpy(path.join(root, "powershell.exe"), true);
    env.SystemRoot = systemRoot;
    env.SYSTEMROOT = systemRoot;
    env.PATH = [decoyBin, "/usr/bin", "/bin"].join(";");
    env.Path = env.PATH;
    env.CODEATLAS_PS_MARKER = systemMarker;
    env.CODEATLAS_DECOY_MARKER = decoyMarker;
    env.CODEATLAS_PS_STDIN = stdinFile;
    const keychain = createKeychain({
      platform: "win32",
      runner: createProcessRunner(env, "win32"),
      env,
    });

    // Act
    keychain.write("openai", secret);

    // Assert
    expect(executable).not.toBeNull();
    expect(existsSync(decoyMarker)).toBe(false);
    const recorded = readFileSync(systemMarker, "utf8");
    expect(recorded).toContain(executable ?? "");
    expect(recorded).toContain("CodeAtlas:ai:openai");
    expect(recorded).toContain("-ExecutionPolicy");
    expect(recorded).not.toContain(secret);
    expect(readFileSync(stdinFile, "utf8")).toBe(secret);
  });

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

function controllableTty(): {
  stdin: SecretInput;
  refs: () => number;
  unrefs: () => number;
  raw: () => boolean;
  paused: () => boolean;
  send: (text: string | null) => void;
} {
  let refs = 0;
  let unrefs = 0;
  let rawMode = false;
  let didPause = false;
  const stream = new Readable({
    read() {
      // send() pushes the typed bytes.
    },
  });
  const stdin = stream as SecretInput;
  stdin.isTTY = true;
  stdin.ref = () => {
    refs += 1;
  };
  stdin.unref = () => {
    unrefs += 1;
  };
  stdin.setRawMode = (mode: boolean) => {
    rawMode = mode;
  };
  const pause = stream.pause.bind(stream);
  stdin.pause = () => {
    didPause = true;
    return pause();
  };
  return {
    stdin,
    refs: () => refs,
    unrefs: () => unrefs,
    raw: () => rawMode,
    paused: () => didPause,
    send(text) {
      stream.push(text);
    },
  };
}

function startOkProvider(): Promise<{
  port: number;
  authorizations: string[];
  close: () => Promise<void>;
}> {
  const authorizations: string[] = [];
  const server = createServer((request, response) => {
    authorizations.push(request.headers.authorization ?? "");
    request.on("data", () => undefined);
    request.on("end", () => {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({
          choices: [{ finish_reason: "stop", message: { content: '{"ok":true}' } }],
        }),
      );
    });
  });
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        reject(new Error("The fake provider did not bind to a port."));
        return;
      }
      resolve({
        port: address.port,
        authorizations,
        close: () =>
          new Promise((done, fail) => {
            server.close((error) => (error ? fail(error) : done()));
          }),
      });
    });
  });
}

function shQuote(value: string): string {
  return `'${value.replace(/'/g, `'\\''`)}'`;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

async function waitForText(read: () => string, needle: string, ms: number): Promise<boolean> {
  const started = Date.now();
  while (Date.now() - started < ms) {
    if (read().includes(needle)) return true;
    await delay(20);
  }
  return false;
}

function waitForClose(child: ChildProcess, ms: number): Promise<number | null> {
  if (child.exitCode !== null || child.signalCode !== null) return Promise.resolve(child.exitCode);
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      child.kill();
      resolve(null);
    }, ms);
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

function installPowerShellSpy(file: string, decoy: boolean): void {
  mkdirSync(path.dirname(file), { recursive: true });
  if (process.platform === "win32") {
    const body = decoy
      ? 'File.WriteAllText(Environment.GetEnvironmentVariable("CODEATLAS_DECOY_MARKER"), "decoy"); return 99;'
      : [
          'var marker = Environment.GetEnvironmentVariable("CODEATLAS_PS_MARKER");',
          'var stdinPath = Environment.GetEnvironmentVariable("CODEATLAS_PS_STDIN");',
          'var recorded = Environment.GetCommandLineArgs()[0] + "\\n" + string.Join("\\n", args);',
          "File.WriteAllText(marker, recorded);",
          "using (var input = Console.OpenStandardInput())",
          "using (var output = File.Create(stdinPath)) { input.CopyTo(output); }",
          "return 0;",
        ].join("\n");
    compileCSharpExe(
      file,
      [
        "using System;",
        "using System.IO;",
        "class FakePs {",
        "  static int Main(string[] args) {",
        body,
        "  }",
        "}",
        "",
      ].join("\n"),
    );
    return;
  }
  const script = decoy
    ? "#!/bin/sh\nprintf '%s\\n' decoy > \"$CODEATLAS_DECOY_MARKER\"\nexit 99\n"
    : [
        "#!/bin/sh",
        'printf "%s\\n" "$0" > "$CODEATLAS_PS_MARKER"',
        'printf "%s\\n" "$@" >> "$CODEATLAS_PS_MARKER"',
        ': > "$CODEATLAS_PS_STDIN"',
        'while IFS= read -r line || [ -n "$line" ]; do',
        '  printf "%s" "$line" >> "$CODEATLAS_PS_STDIN"',
        "done",
        "exit 0",
        "",
      ].join("\n");
  writeFileSync(file, script);
  chmodSync(file, 0o755);
}

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
  try {
    compileCSharpExe(
      file,
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
  } catch (error) {
    const message = error instanceof Error ? error.message : "compile failed";
    throw new Error(`Could not compile ${file} (marker ${marker}): ${message}`);
  }
}
