import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import Database from "better-sqlite3";
import { afterEach, describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { runCli } from "./support/run-cli.js";
import { workspaceStorage } from "./support/storage-name.js";

const symbolKinds = [
  "component",
  "directive",
  "pipe",
  "service",
  "guard",
  "interceptor",
  "route",
  "class",
  "interface",
  "function",
  "method",
  "property",
  "input",
  "output",
  "type-alias",
  "enum",
];

const referenceKinds = [
  "invocation",
  "template-call",
  "template-usage",
  "injection",
  "route",
  "reference",
];

/** Tracked paths for the minimal fixture, from the storage rules in MCP-CONTRACT.md. */
const trackedPaths = ["angular.json", "package.json", "src/cart.ts", "tsconfig.app.json"];

type IndexEnvelope = {
  freshness: { checkedAtUtc: string };
  pagination: { snapshotId: string | null };
  result: { generationId: string; index: string };
};

const cleanups: Array<() => void | Promise<void>> = [];

afterEach(async () => {
  const pending = cleanups.splice(0);
  for (const cleanup of pending.reverse()) await cleanup();
});

function directorySymlinksWork(): boolean {
  const directory = mkdtempSync(path.join(tmpdir(), "codeatlas-ng-symlink-"));
  const link = `${directory}-link`;
  try {
    symlinkSync(directory, link, "dir");
    return true;
  } catch {
    return false;
  } finally {
    rmSync(link, { recursive: true, force: true });
    rmSync(directory, { recursive: true, force: true });
  }
}

const symlinksWork = directorySymlinksWork();

describe("codeatlas-ng index", () => {
  it("publishes a generation for the minimal workspace and prints the index envelope", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const storage = workspaceStorage(workspace.path);

    // Act
    const result = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    const envelope = JSON.parse(result.stdout) as IndexEnvelope;
    expect(envelope.result.generationId).toMatch(/^[0-9a-f]{32}$/);
    expect(envelope.pagination.snapshotId).toBe(envelope.result.generationId);
    expect(envelope.result.index).toBe(storage.currentJson);
    expect(envelope.freshness.checkedAtUtc).toMatch(
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
    );
    expect(readdirSync(path.join(storage.canonical, ".codeatlas", "workspaces"))).toEqual([
      storage.folder,
    ]);
    const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as {
      manifestVersion: number;
      generationId: string;
      schemaVersion: number;
      workspace: string;
      artifacts: { "index.db": { length: number; sha256: string } };
    };
    const databasePath = path.join(
      storage.directory,
      "generations",
      envelope.result.generationId,
      "index.db",
    );
    const databaseBytes = readFileSync(databasePath);
    expect(manifest).toEqual({
      manifestVersion: 1,
      generationId: envelope.result.generationId,
      schemaVersion: 3,
      workspace: storage.identity,
      artifacts: {
        "index.db": {
          length: databaseBytes.length,
          sha256: createHash("sha256").update(databaseBytes).digest("hex"),
        },
      },
    });
    const database = new Database(databasePath, { readonly: true, fileMustExist: true });
    try {
      const metadata = Object.fromEntries(
        database
          .prepare<[], { key: string; value: string }>("SELECT key, value FROM metadata")
          .all()
          .map((row) => [row.key, row.value]),
      );
      expect(metadata).toEqual({
        generationId: envelope.result.generationId,
        schemaVersion: "3",
        contractVersion: "1",
        appVersion: "0.00.01",
        templateCoverage: "complete",
      });
      expect(
        database
          .prepare<[], { path: string }>("SELECT path FROM tracked_files ORDER BY path")
          .all()
          .map((row) => row.path),
      ).toEqual(trackedPaths);
      expect(
        database
          .prepare<[], { id: string; path: string; kind: string }>(
            "SELECT id, path, kind FROM symbols ORDER BY id",
          )
          .all(),
      ).toEqual([{ id: "shop::src/cart.ts#Cart", path: "src/cart.ts", kind: "class" }]);
    } finally {
      database.close();
    }
    expect({
      ...envelope,
      freshness: { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" },
      pagination: { ...envelope.pagination, snapshotId: "GENERATION" },
      result: { ...envelope.result, generationId: "GENERATION", index: "CURRENT_JSON" },
    }).toEqual({
      contractVersion: 1,
      command: "index",
      success: true,
      responseToolVersion: "0.00.01",
      warningCount: 0,
      freshness: {
        state: "fresh",
        checkedAtUtc: "TIMESTAMP",
        isStale: false,
        changedFiles: [],
        scope: "tracked-input-content-hashes",
        inputCoverage: {
          status: "complete",
          scope: "tracked-input-content-hashes",
          issueCount: 0,
          reasonCodes: [],
        },
        diagnostics: [],
      },
      coverage: {
        language: "TypeScript",
        symbols: symbolKinds,
        references: "static-source-and-template-references",
        referenceSymbolKinds: referenceKinds,
        templateCoverage: "complete",
        runtimeCallGraph: false,
        exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
        limitations: [],
      },
      pagination: {
        offset: 0,
        limit: 0,
        returned: 0,
        total: 0,
        hasMore: false,
        nextOffset: null,
        snapshotId: "GENERATION",
        continuationSupported: false,
      },
      result: {
        generationId: "GENERATION",
        schemaVersion: 3,
        contractVersion: 1,
        appVersion: "0.00.01",
        counts: { projects: 1, files: 4, symbols: 1 },
        index: "CURRENT_JSON",
      },
    });
  });

  it("names the folder from the canonical workspace path", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const storage = workspaceStorage(workspace.path);
    const spelled = path.join(workspace.path, "..", path.basename(workspace.path));

    // Act
    const result = await runCli(["index", "--workspace", spelled]);

    // Assert
    expect(result.exitCode).toBe(0);
    expect(readdirSync(path.join(storage.canonical, ".codeatlas", "workspaces"))).toEqual([
      storage.folder,
    ]);
  });

  it("publishes the next generation in the same folder and leaves the first intact", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const first = await runCli(["index", "--workspace", workspace.path]);
    if (first.exitCode !== 0) throw new Error(first.stderr);
    const firstEnvelope = JSON.parse(first.stdout) as IndexEnvelope;
    const storage = workspaceStorage(workspace.path);
    const firstDatabase = path.join(
      storage.directory,
      "generations",
      firstEnvelope.result.generationId,
      "index.db",
    );
    const firstHash = createHash("sha256").update(readFileSync(firstDatabase)).digest("hex");

    // Act
    const second = await runCli(["index", "--workspace", workspace.path]);

    // Assert
    expect(second.exitCode).toBe(0);
    const secondEnvelope = JSON.parse(second.stdout) as IndexEnvelope;
    expect(secondEnvelope.result.generationId).toMatch(/^[0-9a-f]{32}$/);
    expect(secondEnvelope.result.generationId).not.toBe(firstEnvelope.result.generationId);
    expect(readdirSync(path.join(storage.canonical, ".codeatlas", "workspaces"))).toEqual([
      storage.folder,
    ]);
    expect(createHash("sha256").update(readFileSync(firstDatabase)).digest("hex")).toBe(firstHash);
    const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as {
      generationId: string;
    };
    expect(manifest.generationId).toBe(secondEnvelope.result.generationId);
    expect(secondEnvelope.result.index).toBe(storage.currentJson);
  });

  it.skipIf(!symlinksWork)("indexes a symlink at the canonical workspace folder", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    cleanups.push(workspace.remove);
    const storage = workspaceStorage(workspace.path);
    const link = path.join(path.dirname(workspace.path), `${path.basename(workspace.path)}-link`);
    symlinkSync(workspace.path, link, "dir");
    cleanups.push(() => rmSync(link, { force: true }));

    // Act
    const result = await runCli(["index", "--workspace", link]);

    // Assert
    expect(result.exitCode).toBe(0);
    expect(readdirSync(path.join(storage.canonical, ".codeatlas", "workspaces"))).toEqual([
      storage.folder,
    ]);
    expect(JSON.parse(result.stdout).result.index).toBe(storage.currentJson);
  });
});
