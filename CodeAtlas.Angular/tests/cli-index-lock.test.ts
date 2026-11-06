import { createHash } from "node:crypto";
import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { runCli } from "./support/run-cli.js";
import { workspaceStorage } from "./support/storage-name.js";

const cleanups: Array<() => void | Promise<void>> = [];

afterEach(async () => {
  const pending = cleanups.splice(0);
  for (const cleanup of pending.reverse()) await cleanup();
});

describe("codeatlas-ng index writer lock", () => {
  it("refuses a concurrent index and leaves the published generation intact", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const first = await runCli(["index", "--workspace", workspace.path]);
    if (first.exitCode !== 0) throw new Error(first.stderr);
    const firstEnvelope = JSON.parse(first.stdout) as { result: { generationId: string } };
    const storage = workspaceStorage(workspace.path);
    const generationDir = path.join(storage.directory, "generations");
    const firstDatabase = path.join(generationDir, firstEnvelope.result.generationId, "index.db");
    const firstHash = createHash("sha256").update(readFileSync(firstDatabase)).digest("hex");
    const pointer = readFileSync(storage.currentJson, "utf8");
    writeFileSync(path.join(storage.directory, ".write.lock"), `${process.pid}\n`);

    // Act
    const second = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(second.exitCode).toBe(1);
    expect(second.stdout).toBe("");
    expect(JSON.parse(second.stderr)).toEqual({
      contractVersion: 1,
      command: "index",
      success: false,
      responseToolVersion: "0.00.01",
      warningCount: 0,
      freshness: null,
      coverage: null,
      error: {
        code: "index_busy",
        message: "Another index is publishing this workspace.",
        diagnostics: [],
      },
    });
    expect(readFileSync(storage.currentJson, "utf8")).toBe(pointer);
    expect(createHash("sha256").update(readFileSync(firstDatabase)).digest("hex")).toBe(firstHash);
    expect(readdirSync(generationDir).sort()).toEqual([firstEnvelope.result.generationId]);
  });
});
