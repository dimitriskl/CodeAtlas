import {
  existsSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { CommandError } from "./command-error.js";

export type AiPreferences = {
  provider: string;
  model: string;
  effort: string | null;
  endpoint: string;
};

const settingsLimit = 65_536;
const invalidSettings =
  "AI appsettings.json is invalid or exceeds 64 KiB; its contents were not printed or replaced.";

export function defaultSettingsPath(cwd: string): string {
  return path.join(cwd, ".codeatlas", "appsettings.json");
}

export function loadAiSettings(filePath: string): {
  root: Record<string, unknown>;
  ai: AiPreferences | null;
} {
  if (!existsSync(filePath)) return { root: {}, ai: null };
  let text: string;
  try {
    if (statSync(filePath).size > settingsLimit) throw new Error("too large");
    text = readFileSync(filePath, "utf8");
  } catch {
    throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  let root: unknown;
  try {
    root = JSON.parse(text);
  } catch {
    throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  if (!isJsonObject(root)) throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  if (root.ai === undefined) return { root, ai: null };
  return { root, ai: parseAi(root.ai) };
}

export function saveAiSettings(filePath: string, preferences: AiPreferences): void {
  const { root } = loadAiSettings(filePath);
  root.ai = {
    provider: preferences.provider,
    model: preferences.model,
    effort: preferences.effort,
    endpoint: preferences.endpoint,
  };
  const full = path.resolve(filePath);
  mkdirSync(path.dirname(full), { recursive: true });
  const temporary = `${full}.${crypto.randomUUID()}.tmp`;
  const content = `${JSON.stringify(root, null, 2)}\n`;
  try {
    writeFileSync(temporary, content);
    rmSync(full, { force: true });
    renameSync(temporary, full);
  } finally {
    if (existsSync(temporary)) rmSync(temporary, { force: true });
  }
}

function parseAi(value: unknown): AiPreferences {
  if (!isJsonObject(value)) throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  const allowed = new Set(["provider", "model", "effort", "endpoint"]);
  for (const key of Object.keys(value)) {
    if (!allowed.has(key)) throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  if (typeof value.provider !== "string" || typeof value.model !== "string") {
    throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  const effort = value.effort === undefined || value.effort === null ? null : value.effort;
  const endpoint = value.endpoint === undefined || value.endpoint === null ? null : value.endpoint;
  if (effort !== null && typeof effort !== "string") {
    throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  if (endpoint !== null && typeof endpoint !== "string") {
    throw new CommandError("ai_configuration_invalid", invalidSettings, 1);
  }
  return { provider: value.provider, model: value.model, effort, endpoint: endpoint ?? "" };
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
