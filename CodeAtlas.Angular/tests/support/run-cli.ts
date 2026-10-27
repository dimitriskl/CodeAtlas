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
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [cli, ...args], {
      cwd: options.cwd ?? packageRoot,
      env: options.env ?? process.env,
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
    if (options.holdStdin === true) return;
    if (options.stdin === undefined) child.stdin.end();
    else child.stdin.end(options.stdin);
  });
}
