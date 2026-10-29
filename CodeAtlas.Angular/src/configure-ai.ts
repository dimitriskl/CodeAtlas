import { spawnSync } from "node:child_process";
import { fstatSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import type { Readable } from "node:stream";
import { testAiConnection } from "./ai-provider.js";
import {
  defaultSettingsPath,
  loadAiSettings,
  saveAiSettings,
  type AiPreferences,
} from "./ai-settings.js";
import { configureAiSuccessEnvelope, type ConfigureAiSuccessEnvelope } from "./envelope.js";
import { CommandError } from "./errors.js";
import { defaultCredentialsPath, findCredential } from "./credentials.js";
import { createKeychain, type Keychain } from "./keychain.js";
import { createProcessRunner } from "./process-runner.js";

const selectionFlags = ["--provider", "--model", "--effort", "--endpoint", "--settings"] as const;

/** .NET AiConfiguration.Invalid message. The endpoint itself is never included. */
export const insecureEndpointMessage =
  "AI requires a model and a valid cloud-provider key; its endpoint must use HTTPS (HTTP is allowed only for loopback). Ollama needs no key.";

const providers = ["openai", "azure", "anthropic", "kimi", "ollama", "openrouter"] as const;

const effortLevels: Record<string, readonly string[]> = {
  anthropic: ["low", "medium", "high", "xhigh", "max"],
  ollama: ["none", "low", "medium", "high"],
  kimi: ["none", "low", "high", "max"],
};

export function parseConfigureAiArgs(args: readonly string[]): Map<string, string> {
  const options = new Map<string, string>();
  for (let index = 0; index < args.length; index += 1) {
    const flag = args[index];
    const value = args[index + 1];
    if (
      flag === undefined ||
      !flag.startsWith("--") ||
      !selectionFlags.some((allowed) => allowed === flag) ||
      value === undefined ||
      value.startsWith("--") ||
      value.trim().length === 0 ||
      options.has(flag)
    ) {
      throw new CommandError(
        "invalid_arguments",
        "configure-ai accepts --provider, --model, --effort, --endpoint and --settings.",
        2,
      );
    }
    options.set(flag, value);
    index += 1;
  }
  return options;
}

export function assertAiEndpoint(endpoint: string): URL {
  let url: URL;
  try {
    url = new URL(endpoint);
  } catch {
    throw new CommandError("ai_configuration_invalid", "AI endpoint must be an absolute URL.", 1);
  }
  const hostname = url.hostname.replace(/^\[|\]$/g, "");
  const loopback =
    hostname.toLowerCase() === "localhost" ||
    hostname.toLowerCase() === "::1" ||
    /^127(?:\.\d{1,3}){3}$/.test(hostname);
  if (
    url.username.length > 0 ||
    url.password.length > 0 ||
    url.search.length > 0 ||
    url.hash.length > 0 ||
    hostname.length > 180 ||
    !(url.protocol === "https:" || (url.protocol === "http:" && loopback))
  ) {
    throw new CommandError("ai_configuration_invalid", insecureEndpointMessage, 1);
  }
  return url;
}

export type ConfigureAiDependencies = {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  platform?: NodeJS.Platform;
  stdin?: SecretInput;
  stderr?: NodeJS.WritableStream;
  keychain?: Keychain;
  fetchImpl?: typeof fetch;
  home?: string;
};

/** A terminal or pipe the command can read a key from. Tests pass a simulated TTY. */
export type SecretInput = Readable & {
  isTTY?: boolean;
  fd?: number;
  setRawMode?: (mode: boolean) => void;
  ref?: () => void;
  unref?: () => void;
};

const secretLimit = 8192;

export async function runConfigureAi(
  args: readonly string[],
  dependencies: ConfigureAiDependencies = {},
): Promise<ConfigureAiSuccessEnvelope> {
  const cwd = dependencies.cwd ?? process.cwd();
  const env = dependencies.env ?? process.env;
  const platform = dependencies.platform ?? process.platform;
  const stdin = dependencies.stdin ?? (process.stdin as SecretInput);
  const stderr = dependencies.stderr ?? process.stderr;
  stdin.unref?.();
  const options = parseConfigureAiArgs(args);
  const settingsPath = path.resolve(options.get("--settings") ?? defaultSettingsPath(cwd));
  const saved = loadAiSettings(settingsPath).ai;
  const selected = selectPreferences(options, saved);
  const variable = `CODEATLAS_AI_${selected.provider.toUpperCase()}_KEY`;
  const keychain =
    dependencies.keychain ??
    createKeychain({
      platform,
      runner: createProcessRunner(env, platform),
      env,
    });
  const credentialsPath = defaultCredentialsPath({
    platform,
    env,
    home: dependencies.home ?? homedir(),
  });
  const readScopedEnvironment = (name: string, scope: "process" | "user"): string | undefined => {
    if (scope === "process") return env[name];
    if (platform !== "win32") return undefined;
    return readWindowsUserEnvironment(name, env, cwd);
  };
  let key: string | null = null;
  let location: string | null = null;
  let entered = false;
  if (selected.provider !== "ollama") {
    const found = findCredential(selected.provider, {
      platform,
      readEnvironment: readScopedEnvironment,
      keychain,
      credentialsPath,
    });
    if (found !== null) {
      key = found.value;
      location = found.location;
    } else {
      const typed = await resolveMissingKey(variable, keychain, stdin, stderr);
      if (typed === null || typed.length === 0) {
        if (!keychain.isAvailable()) {
          saveAiSettings(settingsPath, selected);
          throw new CommandError("ai_configuration_invalid", noKeychainMessage(variable), 1);
        }
        throw new CommandError(
          "ai_configuration_invalid",
          stdin.isTTY === true
            ? `An API key is required. Set ${variable}.`
            : `An API key is required. Run configure-ai in an interactive terminal or set ${variable}.`,
          1,
        );
      }
      key = typed;
      entered = true;
    }
    assertKey(key);
  }
  assertModel(selected.model);
  await testAiConnection(
    {
      provider: selected.provider,
      model: selected.model,
      endpoint: selected.endpoint,
      effort: selected.effort,
      apiKey: key,
    },
    dependencies.fetchImpl,
  );
  if (entered && key !== null) {
    if (!keychain.isAvailable()) {
      saveAiSettings(settingsPath, selected);
      throw new CommandError("ai_configuration_invalid", noKeychainMessage(variable), 1);
    }
    location = saveKey(keychain, selected.provider, key);
  }
  saveAiSettings(settingsPath, selected);
  return configureAiSuccessEnvelope({
    provider: selected.provider,
    model: selected.model,
    effort: selected.effort,
    endpoint: selected.endpoint,
    settings: settingsPath,
    credentialEnvironmentVariable: selected.provider === "ollama" ? null : variable,
    credentialStore: location,
  });
}

function noKeychainMessage(variable: string): string {
  return `No OS keychain is available, so the API key was not saved. Set ${variable}.`;
}

/**
 * A missing key is prompted only when it can be saved and stdin is a terminal.
 * A pipe or file is read through EOF. An open terminal is never waited on here.
 */
async function resolveMissingKey(
  variable: string,
  keychain: Keychain,
  stdin: SecretInput,
  stderr: NodeJS.WritableStream,
): Promise<string | null> {
  if (!keychain.isAvailable()) {
    if (stdin.isTTY === true) return null;
    return readPipedKey(stdin);
  }
  if (stdin.isTTY === true) return readSecret(variable, stdin, stderr);
  return readPipedKey(stdin);
}

function selectPreferences(
  options: Map<string, string>,
  saved: AiPreferences | null,
): AiPreferences {
  const provider = normalizeProvider(options.get("--provider") ?? saved?.provider ?? "openai");
  const sameProvider = saved !== null && normalizeProvider(saved.provider) === provider;
  const model = options.get("--model") ?? (sameProvider ? saved.model : undefined);
  if (model === undefined) {
    throw new CommandError(
      "invalid_arguments",
      "configure-ai requires --model and an optional --provider.",
      2,
    );
  }
  const requestedEffort = options.get("--effort") ?? (sameProvider ? saved.effort : null);
  const effort = requestedEffort === "default" ? null : requestedEffort;
  validateEffort(provider, effort);
  const endpoint = assertAiEndpoint(
    options.get("--endpoint") ??
      (sameProvider && saved.endpoint.length > 0 ? saved.endpoint : defaultEndpoint(provider)),
  ).href;
  return { provider, model, effort, endpoint };
}

function normalizeProvider(provider: string): string {
  const value = provider.trim().toLowerCase();
  if (value === "llama") return "ollama";
  if ((providers as readonly string[]).includes(value)) return value;
  throw new CommandError(
    "ai_configuration_invalid",
    "Provider must be openai, azure, anthropic, kimi, ollama (or llama), or openrouter.",
    1,
  );
}

function defaultEndpoint(provider: string): string {
  switch (provider) {
    case "openai":
      return "https://api.openai.com/v1/chat/completions";
    case "anthropic":
      return "https://api.anthropic.com/v1/messages";
    case "kimi":
      return "https://api.moonshot.ai/v1/chat/completions";
    case "ollama":
      return "http://localhost:11434/v1/chat/completions";
    case "openrouter":
      return "https://openrouter.ai/api/v1/chat/completions";
    default:
      throw new CommandError(
        "ai_configuration_invalid",
        "Azure requires --endpoint with its full /openai/v1/chat/completions URL and a deployment name as --model.",
        1,
      );
  }
}

function validateEffort(provider: string, effort: string | null): void {
  if (effort === null) return;
  const allowed = effortLevels[provider] ?? [
    "none",
    "minimal",
    "low",
    "medium",
    "high",
    "xhigh",
    "max",
  ];
  if (!allowed.includes(effort)) {
    throw new CommandError(
      "ai_configuration_invalid",
      "Unsupported effort for the selected provider. Use default to omit effort.",
      1,
    );
  }
}

function assertModel(model: string): void {
  if (model.trim().length === 0 || model.length > 200 || hasControlCharacter(model)) {
    throw new CommandError("ai_configuration_invalid", insecureEndpointMessage, 1);
  }
}

function assertKey(key: string): void {
  if (key.trim().length === 0 || key.length > 8192 || /\s/u.test(key) || hasControlCharacter(key)) {
    throw new CommandError("ai_configuration_invalid", insecureEndpointMessage, 1);
  }
}

function hasControlCharacter(value: string): boolean {
  for (const char of value) {
    const code = char.codePointAt(0) ?? 0;
    if (code < 32 || code === 127) return true;
  }
  return false;
}

function saveKey(keychain: Keychain, provider: string, secret: string): string {
  try {
    keychain.write(provider, secret);
  } catch (error) {
    if (error instanceof CommandError) {
      throw new CommandError(
        "ai_configuration_invalid",
        `Connection passed, but the key could not be saved: ${error.message} Settings were not replaced.`,
        1,
      );
    }
    throw error;
  }
  return keychain.name;
}

function readWindowsUserEnvironment(
  name: string,
  env: NodeJS.ProcessEnv,
  cwd: string,
): string | undefined {
  if (!/^[A-Za-z0-9_]+$/.test(name)) return undefined;
  const executable = windowsRegExecutable(env);
  if (executable === null) return undefined;
  const result = spawnSync(executable, ["query", "HKCU\\Environment", "/v", name], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 10_000,
    cwd,
    env,
    shell: false,
  });
  const stdout = result.stdout ?? "";
  if (result.error !== undefined || result.status !== 0 || stdout.length === 0) return undefined;
  const match = new RegExp(String.raw`^\s+${name}\s+REG_(SZ|EXPAND_SZ)\s+(.*)$`, "im").exec(stdout);
  if (match === null) return undefined;
  const value = match[2] ?? "";
  if (match[1] !== "EXPAND_SZ") return value;
  return value.replace(/%([^%]+)%/g, (_match, variable: string) => env[variable] ?? "");
}

/** Absolute System32 reg.exe from %SystemRoot%. A bare name would be found in the workspace first. */
function windowsRegExecutable(env: NodeJS.ProcessEnv): string | null {
  const root = env.SystemRoot ?? env.SYSTEMROOT;
  if (typeof root !== "string" || root.length === 0) return null;
  const executable = path.join(root, "System32", "reg.exe");
  return path.isAbsolute(executable) ? executable : null;
}

async function readPipedKey(stdin: SecretInput): Promise<string | null> {
  if (stdin.isTTY === true) return null;
  const fd = stdin.fd;
  if (fd === undefined) return null;
  let stat: ReturnType<typeof fstatSync>;
  try {
    stat = fstatSync(fd);
  } catch {
    return null;
  }
  // A regular file, including an empty redirect, can be read in full. A pipe must not be:
  // Windows pipes are not FIFOs, and readFileSync on an open pipe waits until it closes.
  if (stat.isFile()) {
    try {
      return normalizeStreamedKey(readFileSync(fd, "utf8"));
    } catch {
      return null;
    }
  }
  // Read a pipe through EOF. A short timer would cut a slow writer off mid-key.
  // An already-closed empty pipe ends immediately, so a missing key does not wait.
  stdin.ref?.();
  stdin.setEncoding("utf8");
  return new Promise((resolve) => {
    let settled = false;
    let data = "";
    const finish = (value: string | null) => {
      if (settled) return;
      settled = true;
      stdin.off("readable", onReadable);
      stdin.off("end", onEnd);
      stdin.pause();
      stdin.unref?.();
      resolve(value);
    };
    const take = (): boolean => {
      let chunk = stdin.read() as string | null;
      while (chunk !== null) {
        data += chunk;
        if (data.includes("\n") || data.includes("\r")) {
          finish(normalizeStreamedKey(data));
          return true;
        }
        chunk = stdin.read() as string | null;
      }
      return false;
    };
    const onReadable = () => {
      take();
    };
    const onEnd = () => {
      take();
      finish(normalizeStreamedKey(data));
    };
    stdin.on("readable", onReadable);
    stdin.on("end", onEnd);
    if (stdin.readableEnded === true) onEnd();
    else take();
  });
}

function normalizeStreamedKey(data: string): string | null {
  let text = data;
  if (text.endsWith("\r\n")) text = text.slice(0, -2);
  else if (text.endsWith("\n")) text = text.slice(0, -1);
  else if (text.endsWith("\r")) text = text.slice(0, -1);
  return text.length === 0 ? null : text;
}

/** .NET AiSetup.ReadSecret: prompt on stderr, no echo, Enter submits, Esc or Ctrl+C cancels. */
function readSecret(
  variable: string,
  stdin: SecretInput,
  stderr: NodeJS.WritableStream,
): Promise<string | null> {
  // runConfigureAi unrefs stdin so a closed pipe cannot hold the process open.
  // The prompt must reference it again or Node exits before the key is typed.
  stdin.ref?.();
  stderr.write(`Enter the API key, or set ${variable} (hidden; Esc cancels): `);
  stdin.setRawMode?.(true);
  return new Promise((resolve) => {
    let settled = false;
    const chars: string[] = [];
    const cleanup = () => {
      stdin.off("data", onData);
      stdin.off("end", onCancel);
      stdin.off("close", onCancel);
      stdin.off("error", onCancel);
      try {
        stdin.setRawMode?.(false);
      } catch {
        // The terminal can already be gone when stdin closes.
      }
      stdin.pause();
      stdin.unref?.();
    };
    const finish = (value: string | null) => {
      if (settled) return;
      settled = true;
      stderr.write("\n");
      cleanup();
      resolve(value);
    };
    const onCancel = () => {
      finish(null);
    };
    const onData = (chunk: Buffer | string) => {
      const text = typeof chunk === "string" ? chunk : chunk.toString("utf8");
      for (const char of text) {
        if (char === "\u001b" || char === "\u0003") {
          finish(null);
          return;
        }
        if (char === "\r" || char === "\n") {
          finish(chars.join(""));
          return;
        }
        if (char === "\u007f" || char === "\b") {
          chars.pop();
          continue;
        }
        const code = char.codePointAt(0) ?? 0;
        if (code < 32 || code === 127) continue;
        if (chars.length < secretLimit) chars.push(char);
      }
    };
    stdin.on("data", onData);
    stdin.on("end", onCancel);
    stdin.on("close", onCancel);
    stdin.on("error", onCancel);
    stdin.resume();
  });
}
