import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { holdWriterLock } from "./support/lock-holder.js";
import { packageRoot, runCli } from "./support/run-cli.js";
import { workspaceStorage } from "./support/storage-name.js";

const cleanups: Array<() => void | Promise<void>> = [];

afterEach(async () => {
  const pending = cleanups.splice(0);
  for (const cleanup of pending.reverse()) await cleanup();
});

/** A positive integer that is not a running process, so the lock is stale rather than busy. */
function absentLockPid(): number {
  const candidate = 2_147_483_646;
  try {
    process.kill(candidate, 0);
  } catch (error) {
    const code =
      typeof error === "object" && error !== null && "code" in error ? error.code : undefined;
    if (code !== "EPERM") return candidate;
  }
  throw new Error(`PID ${candidate} is treated as running.`);
}

function errorEnvelope(code: string, message: string) {
  return {
    contractVersion: 1,
    command: "index",
    success: false,
    responseToolVersion: "0.00.01",
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: { code, message, diagnostics: [] },
  };
}

describe("codeatlas-ng index edges", () => {
  it("indexes the current directory when --workspace is omitted", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const storage = workspaceStorage(workspace.path);

    // Act
    const result = await runCli(["index"], workspace.path);

    // Assert
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    expect(JSON.parse(result.stdout).command).toBe("index");
    expect(existsSync(storage.currentJson)).toBe(true);
    expect(existsSync(path.join(storage.directory, ".write.lock"))).toBe(false);
    expect(existsSync(path.join(packageRoot, ".codeatlas"))).toBe(false);
  });

  it("exits 1 with workspace_not_found when the workspace path does not exist", async () => {
    // Arrange
    const missing = path.join(tmpdir(), `codeatlas-ng-missing-${Date.now()}`);

    // Act
    const result = await runCli(["index", "--workspace", missing]);

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual(
      errorEnvelope(
        "workspace_not_found",
        "No Angular workspace configuration (angular.json) was found.",
      ),
    );
    expect(existsSync(missing)).toBe(false);
  });

  it("exits 1 with workspace_not_found for an empty directory and creates no .codeatlas", async () => {
    // Arrange
    const empty = mkdtempSync(path.join(tmpdir(), "codeatlas-ng-empty-"));
    cleanups.push(() => rmSync(empty, { recursive: true, force: true }));

    // Act
    const result = await runCli(["index", "--workspace", empty]);

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual(
      errorEnvelope(
        "workspace_not_found",
        "No Angular workspace configuration (angular.json) was found.",
      ),
    );
    expect(existsSync(path.join(empty, ".codeatlas"))).toBe(false);
  });

  it("exits 1 with workspace_not_found when the path is a file", async () => {
    // Arrange
    const directory = mkdtempSync(path.join(tmpdir(), "codeatlas-ng-file-"));
    cleanups.push(() => rmSync(directory, { recursive: true, force: true }));
    const file = path.join(directory, "notes.txt");
    writeFileSync(file, "not a workspace");

    // Act
    const result = await runCli(["index", "--workspace", file]);

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr).error.code).toBe("workspace_not_found");
    expect(existsSync(path.join(directory, ".codeatlas"))).toBe(false);
  });

  it("exits 1 with analysis_failed when angular.json is not JSON and creates no .codeatlas", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    writeFileSync(path.join(workspace.path, "angular.json"), "{");

    // Act
    const result = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(result.exitCode).toBe(1);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual(
      errorEnvelope("analysis_failed", "angular.json is not valid JSON."),
    );
    expect(existsSync(path.join(workspace.path, ".codeatlas"))).toBe(false);
  });

  it("exits 2 when index arguments are not --workspace <path>", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);

    // Act
    const result = await runCli(["index", "--workspace"]);

    // Assert
    expect(result.exitCode).toBe(2);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual(
      errorEnvelope("invalid_arguments", "Index accepts only --workspace <path>."),
    );
    expect(existsSync(path.join(workspace.path, ".codeatlas"))).toBe(false);
  });

  it("replaces a stale lock whose pid is not running and keeps the first generation", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const first = await runCli(["index", "--workspace", workspace.path]);
    if (first.exitCode !== 0) throw new Error(first.stderr);
    const firstId = (JSON.parse(first.stdout) as { result: { generationId: string } }).result
      .generationId;
    const storage = workspaceStorage(workspace.path);
    const firstDatabase = path.join(storage.directory, "generations", firstId, "index.db");
    const firstHash = createHash("sha256").update(readFileSync(firstDatabase)).digest("hex");
    writeFileSync(path.join(storage.directory, ".write.lock"), `${absentLockPid()}\n`);

    // Act
    const second = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(second.exitCode).toBe(0);
    expect(second.stderr).toBe("");
    const secondId = (JSON.parse(second.stdout) as { result: { generationId: string } }).result
      .generationId;
    expect(secondId).not.toBe(firstId);
    expect(createHash("sha256").update(readFileSync(firstDatabase)).digest("hex")).toBe(firstHash);
    expect(existsSync(path.join(storage.directory, ".write.lock"))).toBe(false);
    const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as {
      generationId: string;
    };
    expect(manifest.generationId).toBe(secondId);
  });

  it("replaces a lock file that is not a single positive integer", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const storage = workspaceStorage(workspace.path);
    mkdirSync(storage.directory, { recursive: true });
    writeFileSync(path.join(storage.directory, ".write.lock"), "not-a-pid\n");

    // Act
    const result = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    expect(existsSync(storage.currentJson)).toBe(true);
    expect(existsSync(path.join(storage.directory, ".write.lock"))).toBe(false);
  });

  it("refuses index_busy while another process holds .write.lock and leaves that lock", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const first = await runCli(["index", "--workspace", workspace.path]);
    if (first.exitCode !== 0) throw new Error(first.stderr);
    const firstId = (JSON.parse(first.stdout) as { result: { generationId: string } }).result
      .generationId;
    const storage = workspaceStorage(workspace.path);
    const firstDatabase = path.join(storage.directory, "generations", firstId, "index.db");
    const firstHash = createHash("sha256").update(readFileSync(firstDatabase)).digest("hex");
    const pointer = readFileSync(storage.currentJson);
    const holder = await holdWriterLock(path.join(storage.directory, ".write.lock"));
    cleanups.push(holder.stop);

    // Act
    const second = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(second.exitCode).toBe(1);
    expect(second.stdout).toBe("");
    expect(JSON.parse(second.stderr)).toEqual(
      errorEnvelope("index_busy", "Another index is publishing this workspace."),
    );
    expect(readFileSync(path.join(storage.directory, ".write.lock"), "utf8")).toBe(
      `${holder.pid}\n`,
    );
    expect(readFileSync(storage.currentJson)).toEqual(pointer);
    expect(createHash("sha256").update(readFileSync(firstDatabase)).digest("hex")).toBe(firstHash);
  });
});
