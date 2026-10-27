import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { testAiConnection } from "./ai-provider.js";
import {
  defaultSettingsPath,
  loadAiSettings,
  saveAiSettings,
  type AiPreferences,
} from "./ai-settings.js";
import { CommandError } from "./command-error.js";
import { defaultCredentialsPath, findCredential } from "./credentials.js";
import { createKeychain, CredentialStoreError, type Keychain } from "./keychain.js";
import { createProcessRunner } from "./process-runner.js";
import { appVersion, contractVersion } from "./version.js";

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

export async function runConfigureAi(args: readonly string[]): Promise<unknown> {
  const options = parseConfigureAiArgs(args);
  const settingsPath = path.resolve(
    options.get("--settings") ?? defaultSettingsPath(process.cwd()),
  );
  const saved = loadAiSettings(settingsPath).ai;
  const selected = selectPreferences(options, saved);
  const variable = `CODEATLAS_AI_${selected.provider.toUpperCase()}_KEY`;
  const keychain = createKeychain({
    platform: process.platform,
    runner: createProcessRunner(process.env, process.platform),
    env: process.env,
  });
  const credentialsPath = defaultCredentialsPath({
    platform: process.platform,
    env: process.env,
    home: homedir(),
  });
  let key: string | null = null;
  let location: string | null = null;
  let entered = false;
  if (selected.provider !== "ollama") {
    const found = findCredential(selected.provider, {
      platform: process.platform,
      readEnvironment: readEnvironment,
      keychain,
      credentialsPath,
    });
    if (found !== null) {
      key = found.value;
      location = found.location;
    } else {
      const typed = readEnteredKey();
      if (typed === null) {
        throw new CommandError(
          "ai_configuration_invalid",
          `An API key is required. Run configure-ai in an interactive terminal or set ${variable}.`,
          1,
        );
      }
      key = typed;
      entered = true;
    }
    assertKey(key);
  }
  assertModel(selected.model);
  await testAiConnection({
    provider: selected.provider,
    model: selected.model,
    endpoint: selected.endpoint,
    effort: selected.effort,
    apiKey: key,
  });
  if (entered && key !== null) {
    if (!keychain.isAvailable()) {
      saveAiSettings(settingsPath, selected);
      throw new CommandError(
        "ai_configuration_invalid",
        `No OS keychain is available, so the API key was not saved. Set ${variable}.`,
        1,
      );
    }
    location = saveKey(keychain, selected.provider, key);
  }
  saveAiSettings(settingsPath, selected);
  return {
    ai: "enabled",
    connection: "tested",
    provider: selected.provider,
    model: selected.model,
    effort: selected.effort,
    endpoint: selected.endpoint,
    settings: settingsPath,
    credentialEnvironmentVariable: selected.provider === "ollama" ? null : variable,
    credentialStore: location,
    credentialWarning: null,
    message: "Connection passed and options saved. No project source was sent.",
    contractVersion,
    command: "configure-ai",
    success: true,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: null,
    coverage: null,
  };
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
    if (error instanceof CredentialStoreError) {
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

function readEnvironment(name: string, scope: "process" | "user"): string | undefined {
  if (scope === "process") return process.env[name];
  if (process.platform !== "win32") return undefined;
  return readWindowsUserEnvironment(name);
}

function readWindowsUserEnvironment(name: string): string | undefined {
  if (!/^[A-Za-z0-9_]+$/.test(name)) return undefined;
  const result = spawnSync("reg", ["query", "HKCU\\Environment", "/v", name], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 10_000,
  });
  if (result.status !== 0 || result.stdout === null || result.stdout.length === 0) return undefined;
  const match = new RegExp(String.raw`^\s+${name}\s+REG_(SZ|EXPAND_SZ)\s+(.*)$`, "im").exec(
    result.stdout,
  );
  if (match === null) return undefined;
  const value = match[2] ?? "";
  if (match[1] !== "EXPAND_SZ") return value;
  return value.replace(/%([^%]+)%/g, (_match, variable: string) => process.env[variable] ?? "");
}

function readEnteredKey(): string | null {
  if (process.stdin.isTTY) return null;
  let data: string;
  try {
    data = readFileSync(0, "utf8");
  } catch {
    return null;
  }
  if (data.endsWith("\r\n")) data = data.slice(0, -2);
  else if (data.endsWith("\n")) data = data.slice(0, -1);
  return data.length === 0 ? null : data;
}
