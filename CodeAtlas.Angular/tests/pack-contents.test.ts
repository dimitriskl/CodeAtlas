import { spawn } from "node:child_process";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { packageRoot } from "./support/run-cli.js";

const publishedPaths = [
  "LICENSE",
  "README.md",
  "dist/ai-provider.d.ts",
  "dist/ai-provider.js",
  "dist/ai-settings.d.ts",
  "dist/ai-settings.js",
  "dist/cli.d.ts",
  "dist/cli.js",
  "dist/configure-ai.d.ts",
  "dist/configure-ai.js",
  "dist/coverage.d.ts",
  "dist/coverage.js",
  "dist/credentials.d.ts",
  "dist/credentials.js",
  "dist/envelope.d.ts",
  "dist/envelope.js",
  "dist/errors.d.ts",
  "dist/errors.js",
  "dist/find-references.d.ts",
  "dist/find-references.js",
  "dist/find-symbol.d.ts",
  "dist/find-symbol.js",
  "dist/generation-store.d.ts",
  "dist/generation-store.js",
  "dist/index-command.d.ts",
  "dist/index-command.js",
  "dist/index.d.ts",
  "dist/index.js",
  "dist/keychain.d.ts",
  "dist/keychain.js",
  "dist/mcp-server.d.ts",
  "dist/mcp-server.js",
  "dist/mcp.d.ts",
  "dist/mcp.js",
  "dist/process-runner.d.ts",
  "dist/process-runner.js",
  "dist/query-page.d.ts",
  "dist/query-page.js",
  "dist/references.d.ts",
  "dist/references.js",
  "dist/symbols.d.ts",
  "dist/symbols.js",
  "dist/version.d.ts",
  "dist/version.js",
  "dist/workspace-storage.d.ts",
  "dist/workspace-storage.js",
  "dist/workspace.d.ts",
  "dist/workspace.js",
  "package.json",
];

function packContents(): Promise<string> {
  const script = path.join(packageRoot, "scripts", "pack-contents.mjs");
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [script], { cwd: packageRoot });
    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];
    child.stdout.on("data", (chunk: Buffer) => {
      stdout.push(chunk);
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderr.push(chunk);
    });
    child.on("error", reject);
    child.on("close", (code) => {
      const output = Buffer.concat(stdout).toString("utf8");
      if (code !== 0) {
        reject(new Error(Buffer.concat(stderr).toString("utf8") || output));
        return;
      }
      resolve(output);
    });
  });
}

function pathsOf(manifest: string): string[] {
  return manifest
    .trim()
    .split("\n")
    .map((line) => line.slice(line.indexOf("  ") + 2));
}

describe("npm pack", () => {
  it("packs the same archive contents twice", async () => {
    // Arrange
    const expectedPaths = publishedPaths;

    // Act
    const [first, second] = await Promise.all([packContents(), packContents()]);

    // Assert
    expect(first).toBe(second);
    expect(pathsOf(first)).toEqual(expectedPaths);
  });
});
