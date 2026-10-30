import { createHash } from "node:crypto";
import { createServer, type IncomingHttpHeaders, type Server } from "node:http";
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { assertAiEndpoint } from "../src/configure-ai.js";
import { runCli } from "./support/run-cli.js";
import { installFakeWindowsPowerShell } from "./support/windows-keychain-tool.js";

const secret = "sk-synthetic-credential-0123456789";

const endpointError =
  "AI requires a model and a valid cloud-provider key; its endpoint must use HTTPS (HTTP is allowed only for loopback). Ollama needs no key.";

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

describe("codeatlas-ng configure-ai", () => {
  it("rejects a non-loopback http endpoint", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const endpoint = "http://provider.example/chat";

    // Act
    const result = await runCli(
      ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
      { cwd: root, env: isolatedEnv(root) },
    );

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toMatchObject({
      contractVersion: 1,
      command: "configure-ai",
      success: false,
      responseToolVersion: "0.00.01",
      warningCount: 0,
      freshness: null,
      coverage: null,
      error: {
        code: "ai_configuration_invalid",
        message: endpointError,
        diagnostics: [],
      },
    });
    expect(result.stderr).not.toContain(endpoint);
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
  });

  it("saves settings but no key when no OS keychain is available", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = isolatedEnv(root);
    env.PATH = path.join(root, "empty-path");
    env.Path = env.PATH;
    mkdirSync(env.PATH, { recursive: true });
    for (const name of credentialVariables) env[name] = "";
    const credentials = credentialsFile(root);
    mkdirSync(path.dirname(credentials), { recursive: true });
    const credentialsOriginal = "{file-canary-not-rewritten\n";
    writeFileSync(credentials, credentialsOriginal);
    const settings = path.join(root, ".codeatlas", "appsettings.json");
    mkdirSync(path.dirname(settings), { recursive: true });
    writeFileSync(settings, `${JSON.stringify({ logging: { level: "warning" } })}\n`);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env, stdin: `${secret}\n` },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    const error = JSON.parse(result.stderr) as { error: { message: string } };
    expect(error.error.message).toContain("CODEATLAS_AI_OPENAI_KEY");
    expect(result.stderr).not.toContain(secret);
    const saved = JSON.parse(readFileSync(settings, "utf8")) as {
      logging: { level: string };
      ai: { provider: string; model: string; endpoint: string };
    };
    expect(saved.logging).toEqual({ level: "warning" });
    expect(saved.ai).toMatchObject({ provider: "openai", model: "test-model", endpoint });
    expect(readFileSync(settings, "utf8")).not.toContain(secret);
    expect(readFileSync(credentials, "utf8")).toBe(credentialsOriginal);
  });

  it.skipIf(process.platform === "darwin")(
    "saves the key in the OS keychain and does not change credentials.json",
    async () => {
      // Arrange
      const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
      const env = blankCredentialEnv(isolatedEnv(root));
      const state = installFakeKeychain(root, env);
      const credentials = credentialsFile(root);
      mkdirSync(path.dirname(credentials), { recursive: true });
      const credentialsOriginal = "{file-canary-not-rewritten\n";
      writeFileSync(credentials, credentialsOriginal);
      const provider = await startProvider();
      const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
      let result: Awaited<ReturnType<typeof runCli>>;
      try {
        // Act
        result = await runCli(
          ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
          { cwd: root, env, stdin: `${secret}\n` },
        );
      } finally {
        await provider.close();
      }

      // Assert
      expect(result.exitCode).toBe(0);
      expect(result.stdout).not.toContain(secret);
      expect(JSON.parse(result.stdout)).toMatchObject({
        success: true,
        command: "configure-ai",
        credentialStore:
          process.platform === "win32" ? "Windows Credential Manager" : "Linux Secret Service",
        credentialWarning: null,
        message: "Connection passed and options saved. No project source was sent.",
      });
      expect(readFileSync(path.join(state, "stdin"), "utf8")).toBe(secret);
      const recordedArgs = readFileSync(path.join(state, "args"), "utf8");
      expect(recordedArgs).not.toContain(secret);
      if (process.platform === "win32") {
        expect(recordedArgs).toContain("CodeAtlas:ai:openai");
        expect(recordedArgs).toContain("codeatlas");
      } else {
        expect(recordedArgs).toBe(
          "store\n--label=CodeAtlas AI key (openai)\nservice\ncodeatlas\nprovider\nopenai\n",
        );
      }
      expect(readFileSync(credentials, "utf8")).toBe(credentialsOriginal);
      expect(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")).not.toContain(
        secret,
      );
    },
  );

  it("sends a connection test prompt that contains no project source", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const canary = "PROJECT_SOURCE_CANARY_cart_total_9f3c";
    mkdirSync(path.join(root, "src"), { recursive: true });
    writeFileSync(
      path.join(root, "src", "cart.service.ts"),
      `export class CartService { total() { return "${canary}"; } }\n`,
    );
    const env = blankCredentialEnv(isolatedEnv(root));
    env.CODEATLAS_AI_OPENAI_KEY = secret;
    env.PATH = path.join(root, "empty-path");
    env.Path = env.PATH;
    mkdirSync(env.PATH, { recursive: true });
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(0);
    const body = provider.bodies[0] ?? "";
    const sent = JSON.parse(body) as { messages: Array<{ role: string; content: string }> };
    expect(sent.messages).toEqual([
      { role: "system", content: 'Return only a JSON object: {"ok":true}.' },
      {
        role: "user",
        content: "Connection test. Do not use tools or obtain any external context.",
      },
    ]);
    expect(body).not.toContain(canary);
    expect(body).not.toContain("cart.service.ts");
    expect(body).not.toContain(secret);
  });
});

const credentialVariables = [
  "CODEATLAS_AI_OPENAI_KEY",
  "CODEATLAS_AI_API_KEY",
  "CODEATLAS_AI_OPENAI_API_KEY",
  "OPENAI_API_KEY",
  "CODEATLAS_AI_AZURE_KEY",
  "CODEATLAS_AI_AZURE_API_KEY",
  "AZURE_OPENAI_API_KEY",
  "AZUREAI_API_KEY",
  "CODEATLAS_AI_ANTHROPIC_KEY",
  "CODEATLAS_AI_ANTHROPIC_API_KEY",
  "ANTHROPIC_API_KEY",
  "CODEATLAS_AI_KIMI_KEY",
  "CODEATLAS_AI_KIMI_API_KEY",
  "MOONSHOT_API_KEY",
  "CODEATLAS_AI_OPENROUTER_KEY",
  "CODEATLAS_AI_OPENROUTER_API_KEY",
  "OPENROUTER_API_KEY",
];

function blankCredentialEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  for (const name of credentialVariables) env[name] = "";
  return env;
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
        'provider=""',
        'prev=""',
        'for arg in "$@"; do',
        '  if [ "$prev" = "provider" ]; then provider="$arg"; fi',
        '  prev="$arg"',
        "done",
        'if [ "$op" = "lookup" ]; then',
        '  if [ -f "$dir/value-$provider" ]; then cat "$dir/value-$provider"; exit 0; fi',
        "  exit 1",
        "fi",
        'printf "%s\\n" "$@" > "$dir/args"',
        'cat > "$dir/stdin"',
        'if [ "$op" = "store" ]; then cp "$dir/stdin" "$dir/value-$provider"; fi',
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

function credentialsFile(root: string): string {
  if (process.platform === "win32") {
    return path.join(root, "appdata", "CodeAtlas", "credentials.json");
  }
  return path.join(root, "xdg", "codeatlas", "credentials.json");
}

function startProvider(failure?: { status: number; body: string }): Promise<{
  port: number;
  bodies: string[];
  headers: IncomingHttpHeaders[];
  close: () => Promise<void>;
}> {
  const bodies: string[] = [];
  const headers: IncomingHttpHeaders[] = [];
  const server: Server = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => {
      chunks.push(chunk);
    });
    request.on("end", () => {
      bodies.push(Buffer.concat(chunks).toString("utf8"));
      headers.push(request.headers);
      if (failure !== undefined) {
        response.writeHead(failure.status, { "content-type": "text/plain" });
        response.end(failure.body);
        return;
      }
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({
          model: "reply-model",
          choices: [
            {
              finish_reason: "stop",
              message: { content: '{"ok":true}' },
            },
          ],
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
        bodies,
        headers,
        close: () =>
          new Promise((done, fail) => {
            server.close((error) => (error ? fail(error) : done()));
          }),
      });
    });
  });
}

function headerValue(headers: IncomingHttpHeaders, name: string): string | undefined {
  const value = headers[name];
  return Array.isArray(value) ? value[0] : value;
}

function fileFingerprint(file: string): { sha256: string; mtimeNs: bigint } {
  return {
    sha256: createHash("sha256").update(readFileSync(file)).digest("hex"),
    mtimeNs: statSync(file, { bigint: true }).mtimeNs,
  };
}

function writeEncodedCredentials(file: string, keys: Record<string, string>): void {
  mkdirSync(path.dirname(file), { recursive: true });
  const encoded: Record<string, string> = {};
  for (const [provider, value] of Object.entries(keys)) {
    encoded[provider] = `b64:${Buffer.from(value, "utf8").toString("base64")}`;
  }
  writeFileSync(file, `${JSON.stringify({ version: 1, keys: encoded }, null, 2)}\n`);
}

const envSecret = "sk-env-source-111";
const keychainSecret = "sk-keychain-source-222";
const fileSecret = "sk-file-source-333";
const stdinSecret = "sk-stdin-source-444";

describe("configure-ai credential precedence", () => {
  it("sends the environment key and does not rewrite credentials.json", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    env.CODEATLAS_AI_OPENAI_KEY = envSecret;
    const state = installFakeKeychain(root, env);
    writeFileSync(path.join(state, "value-openai"), keychainSecret);
    const credentials = credentialsFile(root);
    writeEncodedCredentials(credentials, { openai: fileSecret });
    const before = fileFingerprint(credentials);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env, stdin: `${stdinSecret}\n` },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(0);
    expect(`${result.stdout}${result.stderr}`).not.toContain(envSecret);
    expect(result.stdout).not.toContain(keychainSecret);
    expect(result.stdout).not.toContain(fileSecret);
    expect(result.stdout).not.toContain(stdinSecret);
    expect(headerValue(provider.headers[0] ?? {}, "authorization")).toBe(`Bearer ${envSecret}`);
    expect(provider.bodies[0] ?? "").not.toContain(envSecret);
    expect(existsSync(path.join(state, "args"))).toBe(false);
    expect(readFileSync(path.join(state, "value-openai"), "utf8")).toBe(keychainSecret);
    expect(fileFingerprint(credentials)).toEqual(before);
  });

  it("sends the keychain key when the environment is empty and leaves credentials.json unchanged", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    const state = installFakeKeychain(root, env);
    writeFileSync(path.join(state, "value-openai"), keychainSecret);
    const credentials = credentialsFile(root);
    writeEncodedCredentials(credentials, { openai: fileSecret, anthropic: "other-provider-key" });
    const before = fileFingerprint(credentials);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env, stdin: `${stdinSecret}\n` },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(0);
    expect(headerValue(provider.headers[0] ?? {}, "authorization")).toBe(
      `Bearer ${keychainSecret}`,
    );
    expect(provider.bodies[0] ?? "").not.toContain(keychainSecret);
    expect(`${result.stdout}${result.stderr}`).not.toContain(keychainSecret);
    expect(result.stdout).not.toContain(fileSecret);
    expect(result.stdout).not.toContain(stdinSecret);
    expect(existsSync(path.join(state, "stdin"))).toBe(false);
    expect(readFileSync(path.join(state, "value-openai"), "utf8")).toBe(keychainSecret);
    expect(fileFingerprint(credentials)).toEqual(before);
  });

  it("sends a credentials.json key and does not rewrite the file", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    env.PATH = path.join(root, "empty-path");
    env.Path = env.PATH;
    mkdirSync(env.PATH, { recursive: true });
    const credentials = credentialsFile(root);
    writeEncodedCredentials(credentials, { openai: fileSecret });
    const before = fileFingerprint(credentials);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env, stdin: `${stdinSecret}\n` },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(0);
    expect(headerValue(provider.headers[0] ?? {}, "authorization")).toBe(`Bearer ${fileSecret}`);
    expect(provider.bodies[0] ?? "").not.toContain(fileSecret);
    expect(provider.bodies[0] ?? "").not.toContain(stdinSecret);
    expect(`${result.stdout}${result.stderr}`).not.toContain(fileSecret);
    expect(result.stdout).not.toContain(stdinSecret);
    expect(fileFingerprint(credentials)).toEqual(before);
  });

  it("does not create credentials.json when the key is stored in the keychain", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    const state = installFakeKeychain(root, env);
    const credentials = credentialsFile(root);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env, stdin: `${secret}\n` },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(0);
    expect(readFileSync(path.join(state, "stdin"), "utf8")).toBe(secret);
    expect(readFileSync(path.join(state, "args"), "utf8")).not.toContain(secret);
    expect(existsSync(credentials)).toBe(false);
    expect(result.stdout).not.toContain(secret);
    expect(readFileSync(path.join(root, ".codeatlas", "appsettings.json"), "utf8")).not.toContain(
      secret,
    );
  });
});

describe("configure-ai endpoint boundaries", () => {
  it.each([
    ["http://0.0.0.0/v1", endpointError],
    ["http://localhost.evil.com/chat", endpointError],
    ["http://127.0.0.1.evil.com/v1", endpointError],
    ["HTTP://EXAMPLE.COM/v1", endpointError],
    ["ftp://127.0.0.1/v1", endpointError],
    ["http://user:secret@127.0.0.1/v1", endpointError],
    ["not a url", "AI endpoint must be an absolute URL."],
    ["example.com/v1", "AI endpoint must be an absolute URL."],
  ] as const)("rejects %s before any settings are written", async (endpoint, message) => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const credentials = credentialsFile(root);
    writeEncodedCredentials(credentials, { openai: fileSecret });
    const before = fileFingerprint(credentials);

    // Act
    const result = await runCli(
      ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
      { cwd: root, env: isolatedEnv(root), stdin: `${stdinSecret}\n` },
    );

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toMatchObject({
      success: false,
      command: "configure-ai",
      error: { code: "ai_configuration_invalid", message, diagnostics: [] },
    });
    expect(result.stderr).not.toContain(endpoint);
    expect(result.stderr).not.toContain(stdinSecret);
    expect(result.stderr).not.toContain(fileSecret);
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
    expect(fileFingerprint(credentials)).toEqual(before);
  });

  it.each([
    ["http://localhost/v1", "localhost", "http:"],
    ["http://127.0.0.1/v1", "127.0.0.1", "http:"],
    ["http://[::1]/v1", "[::1]", "http:"],
    ["http://127.0.0.2/v1", "127.0.0.2", "http:"],
    ["HTTP://LOCALHOST/v1", "localhost", "http:"],
    ["https://api.example.com/v1", "api.example.com", "https:"],
  ] as const)("accepts %s", (endpoint, hostname, protocol) => {
    // Arrange
    const value = endpoint;

    // Act
    const url = assertAiEndpoint(value);

    // Assert
    expect(url.hostname).toBe(hostname);
    expect(url.protocol).toBe(protocol);
  });
});

describe("configure-ai secret handling", () => {
  it("omits a provider error body that echoes the key", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    env.CODEATLAS_AI_OPENAI_KEY = secret;
    env.PATH = path.join(root, "empty-path");
    env.Path = env.PATH;
    mkdirSync(env.PATH, { recursive: true });
    const provider = await startProvider({ status: 401, body: `ECHO ${secret} denied` });
    const endpoint = `http://127.0.0.1:${provider.port}/v1/chat/completions`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        ["configure-ai", "--provider", "openai", "--model", "test-model", "--endpoint", endpoint],
        { cwd: root, env },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr).error.code).toBe("ai_provider_failed");
    expect(`${result.stdout}${result.stderr}`).not.toContain(secret);
    expect(result.stderr).not.toContain("ECHO");
    expect(headerValue(provider.headers[0] ?? {}, "authorization")).toBe(`Bearer ${secret}`);
    expect(existsSync(path.join(root, ".codeatlas", "appsettings.json"))).toBe(false);
    expect(existsSync(credentialsFile(root))).toBe(false);
  });

  it("sends an anthropic key only in x-api-key", async () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-configure-ai-"));
    const env = blankCredentialEnv(isolatedEnv(root));
    env.CODEATLAS_AI_ANTHROPIC_KEY = secret;
    env.PATH = path.join(root, "empty-path");
    env.Path = env.PATH;
    mkdirSync(env.PATH, { recursive: true });
    const canary = "PROJECT_SOURCE_CANARY_anthropic_9f3c";
    writeFileSync(path.join(root, "component.ts"), `export const marker = "${canary}";\n`);
    const provider = await startProvider();
    const endpoint = `http://127.0.0.1:${provider.port}/v1/messages`;
    let result: Awaited<ReturnType<typeof runCli>>;
    try {
      // Act
      result = await runCli(
        [
          "configure-ai",
          "--provider",
          "anthropic",
          "--model",
          "claude-test",
          "--endpoint",
          endpoint,
        ],
        { cwd: root, env },
      );
    } finally {
      await provider.close();
    }

    // Assert
    expect(result.exitCode).toBe(1);
    const headers = provider.headers[0] ?? {};
    expect(headerValue(headers, "x-api-key")).toBe(secret);
    expect(headerValue(headers, "authorization")).toBeUndefined();
    const body = provider.bodies[0] ?? "";
    expect(body).not.toContain(secret);
    expect(body).not.toContain(canary);
    expect(body).not.toContain("component.ts");
    expect(body).toContain("Connection test. Do not use tools or obtain any external context.");
    expect(`${result.stdout}${result.stderr}`).not.toContain(secret);
  });
});
