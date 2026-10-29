import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const packageRoot = fileURLToPath(new URL("../..", import.meta.url));

export function readPackageVersion(): string {
  const manifest = JSON.parse(readFileSync(path.join(packageRoot, "package.json"), "utf8")) as {
    version: string;
  };
  return manifest.version;
}

export type RunCliOptions = {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  stdin?: string;
  /** Keep the stdin pipe open so a missing key must not wait for it. */
  holdStdin?: boolean;
  /** Write stdin in pieces. The last piece that sets `end` closes the pipe. */
  stdinWrites?: readonly { delayMs: number; text: string; end?: boolean }[];
  timeoutMs?: number;
};

export function runCli(
  args: readonly string[],
  cwdOrOptions: string | RunCliOptions = {},
): Promise<{
  exitCode: number;
  stdout: string;
  stderr: string;
}> {
  const options: RunCliOptions =
    typeof cwdOrOptions === "string" ? { cwd: cwdOrOptions } : cwdOrOptions;
  const cli = path.join(packageRoot, "dist", "cli.js");
  const env = childEnv(options.env ?? process.env);
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [cli, ...args], {
      cwd: options.cwd ?? packageRoot,
      env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];
    child.stdout.on("data", (chunk: Buffer) => {
      stdout.push(chunk);
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderr.push(chunk);
    });
    child.on("error", reject);
    let timer: NodeJS.Timeout | undefined;
    if (options.timeoutMs !== undefined) {
      timer = setTimeout(() => {
        child.kill();
      }, options.timeoutMs);
    }
    child.on("close", (code) => {
      if (timer !== undefined) clearTimeout(timer);
      resolve({
        exitCode: code ?? 1,
        stdout: Buffer.concat(stdout).toString("utf8"),
        stderr: Buffer.concat(stderr).toString("utf8"),
      });
    });
    child.stdin.on("error", () => {
      // The process can exit before the pipe is read.
    });
    if (options.stdinWrites !== undefined) {
      for (const write of options.stdinWrites) {
        const timer = setTimeout(() => {
          if (write.end === true) child.stdin.end(write.text);
          else child.stdin.write(write.text);
        }, write.delayMs);
        child.on("close", () => {
          clearTimeout(timer);
        });
      }
      return;
    }
    if (options.holdStdin === true) return;
    if (options.stdin === undefined) child.stdin.end();
    else child.stdin.end(options.stdin);
  });
}

/**
 * A Windows process aborts if it is started with a SystemRoot that is not the
 * real OS directory. Keep that value for startup, and restore a fixture root
 * before the CLI reads it so tests do not call Credential Manager.
 */
function childEnv(source: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...source };
  if (process.platform !== "win32") return env;
  const real = process.env.SystemRoot ?? process.env.SYSTEMROOT;
  const requested = env.SystemRoot ?? env.SYSTEMROOT;
  if (real === undefined || requested === undefined) return env;
  if (path.normalize(real) === path.normalize(requested)) return env;
  env.CODEATLAS_FAKE_SYSTEM_ROOT = requested;
  env.SystemRoot = real;
  env.SYSTEMROOT = real;
  const preload = path
    .join(packageRoot, "tests", "support", "win-system-root.cjs")
    .replace(/\\/g, "/");
  const requirement = `--require "${preload}"`;
  const existing = env.NODE_OPTIONS ?? "";
  env.NODE_OPTIONS = existing.length === 0 ? requirement : `${existing} ${requirement}`;
  return env;
}
