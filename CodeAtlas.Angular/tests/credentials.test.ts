import { mkdtempSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import {
  createKeychain,
  defaultCredentialsPath,
  findCredential,
  type Keychain,
  type ProcessResult,
  type ProcessRunner,
} from "../src/credentials.js";

/**
 * Environment-variable order from PRD 0001, credential contract.
 * The first non-empty value wins.
 */
const providerKeyVariables = {
  openai: [
    "CODEATLAS_AI_OPENAI_KEY",
    "CODEATLAS_AI_API_KEY",
    "CODEATLAS_AI_OPENAI_API_KEY",
    "OPENAI_API_KEY",
  ],
  azure: [
    "CODEATLAS_AI_AZURE_KEY",
    "CODEATLAS_AI_AZURE_API_KEY",
    "AZURE_OPENAI_API_KEY",
    "AZUREAI_API_KEY",
  ],
  anthropic: ["CODEATLAS_AI_ANTHROPIC_KEY", "CODEATLAS_AI_ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY"],
  kimi: ["CODEATLAS_AI_KIMI_KEY", "CODEATLAS_AI_KIMI_API_KEY", "MOONSHOT_API_KEY"],
  openrouter: [
    "CODEATLAS_AI_OPENROUTER_KEY",
    "CODEATLAS_AI_OPENROUTER_API_KEY",
    "OPENROUTER_API_KEY",
  ],
} as const;

const orderCases = Object.entries(providerKeyVariables).flatMap(([provider, names]) =>
  names.map((name, index) => ({ provider, name, index, names: [...names] })),
);

function emptyKeychain(): Keychain {
  return {
    name: "Fake Keychain",
    isAvailable: () => false,
    read: () => null,
    write: () => {
      throw new Error("Unexpected keychain write");
    },
  };
}

function credentialsPath(): string {
  return path.join(mkdtempSync(path.join(tmpdir(), "codeatlas-cred-")), "credentials.json");
}

describe("credential environment variables", () => {
  it.each(orderCases)(
    "$provider uses $name when every earlier variable is empty",
    ({ provider, name, index, names }) => {
      // Arrange
      const values = new Map<string, string>();
      for (let i = 0; i < names.length; i += 1) {
        const variable = names[i];
        if (variable === undefined) continue;
        values.set(variable, i < index ? " \t" : `value-${i}`);
      }

      // Act
      const found = findCredential(provider, {
        platform: "linux",
        readEnvironment: (variable) => values.get(variable),
        keychain: emptyKeychain(),
        credentialsPath: credentialsPath(),
      });

      // Assert
      expect(found).toEqual({
        value: `value-${index}`,
        location: `environment variable ${name}`,
      });
    },
  );

  it("does not read a key for ollama", () => {
    // Arrange
    const reads: string[] = [];

    // Act
    const found = findCredential("ollama", {
      platform: "linux",
      readEnvironment: (variable) => {
        reads.push(variable);
        return "should-not-be-used";
      },
      keychain: emptyKeychain(),
      credentialsPath: credentialsPath(),
    });

    // Assert
    expect(found).toBeNull();
    expect(reads).toEqual([]);
  });

  it("reads the Windows user-scope value when the process value is absent", () => {
    // Arrange
    const reads: Array<{ name: string; scope: "process" | "user" }> = [];

    // Act
    const found = findCredential("openai", {
      platform: "win32",
      readEnvironment: (name, scope) => {
        reads.push({ name, scope });
        if (scope === "user" && name === "CODEATLAS_AI_OPENAI_KEY") return "user-scope-key";
        return undefined;
      },
      keychain: emptyKeychain(),
      credentialsPath: credentialsPath(),
    });

    // Assert
    expect(found).toEqual({
      value: "user-scope-key",
      location: "environment variable CODEATLAS_AI_OPENAI_KEY",
    });
    expect(reads).toEqual([
      { name: "CODEATLAS_AI_OPENAI_KEY", scope: "process" },
      { name: "CODEATLAS_AI_OPENAI_KEY", scope: "user" },
    ]);
  });

  it("does not consult Windows user scope when the process value is present", () => {
    // Arrange
    const reads: Array<"process" | "user"> = [];

    // Act
    const found = findCredential("openai", {
      platform: "win32",
      readEnvironment: (_name, scope) => {
        reads.push(scope);
        if (scope === "process") return "";
        return "user-scope-key";
      },
      keychain: emptyKeychain(),
      credentialsPath: credentialsPath(),
    });

    // Assert
    expect(found).toBeNull();
    expect(reads).not.toContain("user");
  });

  it("does not read user scope on Linux when the process value is absent", () => {
    // Arrange
    const reads: Array<"process" | "user"> = [];

    // Act
    const found = findCredential("anthropic", {
      platform: "linux",
      readEnvironment: (_name, scope) => {
        reads.push(scope);
        if (scope === "user") return "user-scope-key";
        return undefined;
      },
      keychain: emptyKeychain(),
      credentialsPath: credentialsPath(),
    });

    // Assert
    expect(found).toBeNull();
    expect(reads).not.toContain("user");
  });
});

const secret = "sk-synthetic-credential-0123456789";

type RecordedCall = {
  command: string;
  args: readonly string[];
  stdin: string | null;
};

function recordingRunner(respond: (call: RecordedCall) => ProcessResult): {
  calls: RecordedCall[];
  runner: ProcessRunner;
} {
  const calls: RecordedCall[] = [];
  return {
    calls,
    runner: {
      exists: () => true,
      run: (command, args, stdin) => {
        const call = { command, args, stdin };
        calls.push(call);
        return respond(call);
      },
    },
  };
}

describe("keychain entry names", () => {
  it("stores a macOS keychain secret on stdin under service CodeAtlas and account the provider", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({ exitCode: 0, stdout: "", stderr: "" }));
    const keychain = createKeychain({ platform: "darwin", runner, env: {} });

    // Act
    keychain.write("azure", secret);

    // Assert
    const write = calls.find((call) => call.stdin !== null);
    expect(write).toEqual({
      command: "/usr/bin/security",
      args: ["-i"],
      stdin: `add-generic-password -U -s "CodeAtlas" -a "azure" -l "CodeAtlas AI key (azure)" -w "${secret}"\n`,
    });
    expect(write?.args.join(" ")).not.toContain(secret);
  });

  it("reads a macOS keychain item by service CodeAtlas and account the provider", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({
      exitCode: 0,
      stdout: `${secret}\n`,
      stderr: "",
    }));
    const keychain = createKeychain({ platform: "darwin", runner, env: {} });

    // Act
    const value = keychain.read("azure");

    // Assert
    expect(value).toBe(secret);
    expect(calls).toEqual([
      {
        command: "/usr/bin/security",
        args: ["find-generic-password", "-s", "CodeAtlas", "-a", "azure", "-w"],
        stdin: null,
      },
    ]);
  });

  it("stores a Linux secret-tool secret on stdin with service codeatlas and provider", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({ exitCode: 0, stdout: "", stderr: "" }));
    const keychain = createKeychain({
      platform: "linux",
      runner,
      env: { DBUS_SESSION_BUS_ADDRESS: "unix:path=/run/user/1000/bus" },
    });

    // Act
    keychain.write("azure", secret);

    // Assert
    const write = calls.find((call) => call.stdin !== null);
    expect(write).toEqual({
      command: "secret-tool",
      args: [
        "store",
        "--label=CodeAtlas AI key (azure)",
        "service",
        "codeatlas",
        "provider",
        "azure",
      ],
      stdin: secret,
    });
    expect(write?.args.join(" ")).not.toContain(secret);
  });

  it("reads a Linux secret-tool item by service codeatlas and provider", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({
      exitCode: 0,
      stdout: `${secret}\n`,
      stderr: "",
    }));
    const keychain = createKeychain({
      platform: "linux",
      runner,
      env: { DBUS_SESSION_BUS_ADDRESS: "unix:path=/run/user/1000/bus" },
    });

    // Act
    const value = keychain.read("azure");

    // Assert
    expect(value).toBe(secret);
    expect(calls).toEqual([
      {
        command: "secret-tool",
        args: ["lookup", "service", "codeatlas", "provider", "azure"],
        stdin: null,
      },
    ]);
  });

  it("stores a Windows Credential Manager secret on stdin at target CodeAtlas:ai:<provider>", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({ exitCode: 0, stdout: "", stderr: "" }));
    const keychain = createKeychain({ platform: "win32", runner, env: {} });

    // Act
    keychain.write("azure", secret);

    // Assert
    const write = calls.find((call) => call.stdin !== null);
    expect(write?.command).toBe("powershell");
    expect(write?.stdin).toBe(secret);
    expect(write?.args.slice(0, 5)).toEqual([
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
    ]);
    expect(write?.args).toContain("CodeAtlas:ai:azure");
    expect(write?.args).toContain("codeatlas");
    expect(write?.args.join("\n")).not.toContain(secret);
  });

  it("reads a Windows Credential Manager secret by target CodeAtlas:ai:<provider>", () => {
    // Arrange
    const { calls, runner } = recordingRunner(() => ({
      exitCode: 0,
      stdout: secret,
      stderr: "",
    }));
    const keychain = createKeychain({ platform: "win32", runner, env: {} });

    // Act
    const value = keychain.read("azure");

    // Assert
    expect(value).toBe(secret);
    expect(calls[0]?.command).toBe("powershell");
    expect(calls[0]?.args.slice(0, 5)).toEqual([
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
    ]);
    expect(calls[0]?.args).toContain("CodeAtlas:ai:azure");
    expect(calls[0]?.args).toContain("codeatlas");
    expect(calls[0]?.stdin).toBeNull();
  });
});

describe("credentials.json", () => {
  it("finds a key that exists only in the .NET credentials file and does not rewrite it", () => {
    // Arrange
    const root = mkdtempSync(path.join(tmpdir(), "codeatlas-cred-file-"));
    const file = path.join(root, "credentials.json");
    const encoded = Buffer.from(secret, "utf8").toString("base64");
    const original = `${JSON.stringify(
      {
        version: 1,
        notice:
          "CodeAtlas AI keys. Not encrypted: protected only by owner-only file permissions. Never commit or share this file.",
        keys: { kimi: `b64:${encoded}` },
      },
      null,
      2,
    )}\n`;
    writeFileSync(file, original);

    // Act
    const found = findCredential("kimi", {
      platform: "linux",
      readEnvironment: () => undefined,
      keychain: emptyKeychain(),
      credentialsPath: file,
    });

    // Assert
    expect(found).toEqual({
      value: secret,
      location: `credentials file ${path.resolve(file)}`,
    });
    expect(readFileSync(file, "utf8")).toBe(original);
  });

  it.each([
    {
      label: "Windows APPDATA",
      platform: "win32" as const,
      env: { APPDATA: "C:\\Users\\dev\\AppData\\Roaming" },
      expected: "C:\\Users\\dev\\AppData\\Roaming\\CodeAtlas\\credentials.json",
    },
    {
      label: "XDG_CONFIG_HOME",
      platform: "linux" as const,
      env: { XDG_CONFIG_HOME: "/tmp/cfg" },
      expected: "/tmp/cfg/codeatlas/credentials.json",
    },
    {
      label: "the home config directory when XDG_CONFIG_HOME is not absolute",
      platform: "linux" as const,
      env: { XDG_CONFIG_HOME: "relative" },
      expected: "/home/dev/.config/codeatlas/credentials.json",
    },
  ])("reads $label", ({ platform, env, expected }) => {
    // Arrange
    const home = "/home/dev";

    // Act
    const file = defaultCredentialsPath({ platform, env, home });

    // Assert
    expect(file).toBe(expected);
  });
});

describe("credential lookup precedence", () => {
  it("prefers an environment variable over a different keychain entry and credentials file", () => {
    // Arrange
    const file = credentialsPath();
    const original = encodedCredentials({ openai: "file-key" });
    writeFileSync(file, original);
    const before = statSync(file, { bigint: true }).mtimeNs;
    const reads: string[] = [];
    const keychain: Keychain = {
      name: "Linux Secret Service",
      isAvailable: () => true,
      read: (provider) => {
        reads.push(provider);
        return "keychain-key";
      },
      write: () => {
        throw new Error("Unexpected keychain write");
      },
    };

    // Act
    const found = findCredential("openai", {
      platform: "linux",
      readEnvironment: (name) => (name === "CODEATLAS_AI_OPENAI_KEY" ? "env-key" : " \t"),
      keychain,
      credentialsPath: file,
    });

    // Assert
    expect(found).toEqual({
      value: "env-key",
      location: "environment variable CODEATLAS_AI_OPENAI_KEY",
    });
    expect(reads).toEqual([]);
    expect(readFileSync(file, "utf8")).toBe(original);
    expect(statSync(file, { bigint: true }).mtimeNs).toBe(before);
  });

  it("prefers the keychain over a different credentials file when the environment is empty", () => {
    // Arrange
    const file = credentialsPath();
    const original = encodedCredentials({ openai: "file-key" });
    writeFileSync(file, original);
    const before = statSync(file, { bigint: true }).mtimeNs;

    // Act
    const found = findCredential("openai", {
      platform: "linux",
      readEnvironment: () => "",
      keychain: {
        name: "Linux Secret Service",
        isAvailable: () => true,
        read: () => "keychain-key",
        write: () => {
          throw new Error("Unexpected keychain write");
        },
      },
      credentialsPath: file,
    });

    // Assert
    expect(found).toEqual({ value: "keychain-key", location: "Linux Secret Service" });
    expect(readFileSync(file, "utf8")).toBe(original);
    expect(statSync(file, { bigint: true }).mtimeNs).toBe(before);
  });

  it("uses credentials.json when the keychain value is empty and does not rewrite the file", () => {
    // Arrange
    const file = credentialsPath();
    const original = encodedCredentials({ openai: "file-key" });
    writeFileSync(file, original);
    const before = statSync(file, { bigint: true }).mtimeNs;

    // Act
    const found = findCredential("openai", {
      platform: "linux",
      readEnvironment: () => undefined,
      keychain: {
        name: "Linux Secret Service",
        isAvailable: () => true,
        read: () => " \n",
        write: () => {
          throw new Error("Unexpected keychain write");
        },
      },
      credentialsPath: file,
    });

    // Assert
    expect(found).toEqual({
      value: "file-key",
      location: `credentials file ${path.resolve(file)}`,
    });
    expect(readFileSync(file, "utf8")).toBe(original);
    expect(statSync(file, { bigint: true }).mtimeNs).toBe(before);
  });
});

function encodedCredentials(keys: Record<string, string>): string {
  const encoded: Record<string, string> = {};
  for (const [provider, value] of Object.entries(keys)) {
    encoded[provider] = `b64:${Buffer.from(value, "utf8").toString("base64")}`;
  }
  return `${JSON.stringify({ version: 1, keys: encoded }, null, 2)}\n`;
}
