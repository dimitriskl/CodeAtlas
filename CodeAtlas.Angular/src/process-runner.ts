import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import { CredentialStoreError, type ProcessRunner } from "./keychain.js";

/** Resolves a tool from PATH, checking the earlier directories before later ones. */
export function resolveCommand(
  command: string,
  env: NodeJS.ProcessEnv,
  platform: NodeJS.Platform,
): string | null {
  if (path.isAbsolute(command)) return existsSync(command) ? command : null;
  const pathEnv = env.PATH ?? env.Path ?? "";
  const separator = platform === "win32" ? ";" : ":";
  const extensions = platform === "win32" ? [".exe", ".cmd", ".bat", ".js", ""] : [""];
  for (const directory of pathEnv.split(separator)) {
    if (directory.length === 0) continue;
    for (const extension of extensions) {
      const candidate = path.join(directory, `${command}${extension}`);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

export function createProcessRunner(
  env: NodeJS.ProcessEnv = process.env,
  platform: NodeJS.Platform = process.platform,
): ProcessRunner {
  return {
    exists(command) {
      return resolveCommand(command, env, platform) !== null;
    },
    run(command, args, stdin) {
      const resolved = resolveCommand(command, env, platform);
      const base = path.basename(command);
      if (resolved === null) throw new CredentialStoreError(`${base} could not be started.`);
      const script = resolved.endsWith(".js");
      const result = spawnSync(
        script ? process.execPath : resolved,
        script ? [resolved, ...args] : [...args],
        {
          input: stdin ?? undefined,
          encoding: "utf8",
          timeout: 30_000,
          windowsHide: true,
          shell: false,
          maxBuffer: 1024 * 1024,
        },
      );
      if (result.error !== undefined) {
        const code = (result.error as NodeJS.ErrnoException).code;
        if (code === "ETIMEDOUT") {
          throw new CredentialStoreError(`${base} did not finish within 30 seconds.`);
        }
        throw new CredentialStoreError(`${base} could not be started.`);
      }
      if (result.status === null) throw new CredentialStoreError(`${base} could not be started.`);
      return { exitCode: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
    },
  };
}
