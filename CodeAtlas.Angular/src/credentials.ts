import { existsSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { CredentialStoreError, type Keychain } from "./keychain.js";

export type { Keychain, ProcessResult, ProcessRunner } from "./keychain.js";
export { createKeychain, CredentialStoreError } from "./keychain.js";

export type EnvScope = "process" | "user";

export type EnvironmentReader = (name: string, scope: EnvScope) => string | undefined;

export type CredentialLookup = {
  value: string;
  location: string;
};

const providerKeyVariables: Record<string, readonly string[]> = {
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
};

function hasText(value: string | undefined): value is string {
  return value !== undefined && value.trim().length > 0;
}

function readVariable(
  name: string,
  platform: NodeJS.Platform,
  readEnvironment: EnvironmentReader,
): string | undefined {
  const processValue = readEnvironment(name, "process");
  if (processValue !== undefined) return processValue;
  if (platform !== "win32") return undefined;
  return readEnvironment(name, "user");
}

/**
 * Lookup order matches the .NET edition: environment variables (first non-empty wins,
 * Windows user scope only when the process value is absent), then the keychain, then
 * the read-only credentials file.
 */
export function findCredential(
  provider: string,
  deps: {
    platform: NodeJS.Platform;
    readEnvironment: EnvironmentReader;
    keychain: Keychain;
    credentialsPath: string;
  },
): CredentialLookup | null {
  if (provider === "ollama") return null;
  for (const name of providerKeyVariables[provider] ?? []) {
    const value = readVariable(name, deps.platform, deps.readEnvironment);
    if (hasText(value)) return { value, location: `environment variable ${name}` };
  }
  if (deps.keychain.isAvailable()) {
    try {
      const stored = deps.keychain.read(provider);
      if (stored !== null && hasText(stored))
        return { value: stored, location: deps.keychain.name };
    } catch (error) {
      if (!(error instanceof CredentialStoreError)) throw error;
    }
  }
  const stored = readCredentialsFile(deps.credentialsPath, provider);
  if (stored === null || !hasText(stored)) return null;
  return { value: stored, location: `credentials file ${path.resolve(deps.credentialsPath)}` };
}

const credentialsFileLimit = 65_536;
const base64Prefix = "b64:";

function readCredentialsFile(filePath: string, provider: string): string | null {
  if (!existsSync(filePath)) return null;
  try {
    if (statSync(filePath).size > credentialsFileLimit) return null;
    const root = JSON.parse(readFileSync(filePath, "utf8")) as unknown;
    if (!isJsonObject(root)) return null;
    const keys = root.keys;
    if (keys === undefined) return null;
    if (!isJsonObject(keys)) return null;
    const stored = keys[provider];
    if (typeof stored !== "string") return null;
    return decodeStoredKey(stored);
  } catch {
    return null;
  }
}

function decodeStoredKey(stored: string): string {
  if (!stored.startsWith(base64Prefix)) return stored;
  const encoded = stored.slice(base64Prefix.length);
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encoded)) {
    throw new CredentialStoreError("The credentials file is invalid.");
  }
  return Buffer.from(encoded, "base64").toString("utf8");
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** .NET fallback file. This edition reads it and never writes or deletes it. */
export function defaultCredentialsPath(deps: {
  platform: NodeJS.Platform;
  env: NodeJS.ProcessEnv;
  home: string;
}): string {
  const paths = deps.platform === "win32" ? path.win32 : path.posix;
  if (deps.platform === "win32") {
    const appData = deps.env.APPDATA;
    const root =
      appData !== undefined && appData.trim().length > 0 && paths.isAbsolute(appData)
        ? appData
        : paths.join(deps.home, "AppData", "Roaming");
    return paths.join(root, "CodeAtlas", "credentials.json");
  }
  const configured = deps.env.XDG_CONFIG_HOME;
  const config =
    configured !== undefined && configured.trim().length > 0 && paths.isAbsolute(configured)
      ? configured
      : paths.join(deps.home, ".config");
  return paths.join(config, "codeatlas", "credentials.json");
}
