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
