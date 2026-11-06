import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import Database from "better-sqlite3";
import { describe, expect, it } from "vitest";
import { workspaceStorage } from "../src/workspace-storage.js";
import { copySymbolsWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

type Manifest = {
  manifestVersion: number;
  generationId: string;
  schemaVersion?: unknown;
  workspace: string;
  artifacts: { "index.db": { length: number; sha256: string } };
};

type ErrorEnvelope = {
  contractVersion: number;
  command: string;
  success: boolean;
  responseToolVersion: string;
  warningCount: number;
  freshness: null;
  coverage: null;
  error: { code: string; message: string; diagnostics: string[] };
};

function isTextBlock(block: unknown): block is { type: "text"; text: string } {
  return (
    typeof block === "object" &&
    block !== null &&
    "type" in block &&
    block.type === "text" &&
    "text" in block &&
    typeof block.text === "string"
  );
}

function parseToolText(result: unknown): ErrorEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("tool did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("tool did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("tool did not return a text block");
  return JSON.parse(block.text) as ErrorEnvelope;
}

function errorEnvelope(command: string, code: string, message: string): ErrorEnvelope {
  return {
    contractVersion: 1,
    command,
    success: false,
    responseToolVersion: "0.00.01",
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: {
      code,
      message,
      diagnostics: [],
    },
  };
}

function unsupportedMessage(workspacePath: string): string {
  return `Unsupported index schema. Run codeatlas-ng index --workspace ${workspacePath} to rebuild it.`;
}

async function indexedSymbolsWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  const workspace = await copySymbolsWorkspace();
  const indexed = await runCli(["index", "--workspace", workspace.path]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
  return workspace;
}

function readManifest(workspacePath: string): {
  storage: ReturnType<typeof workspaceStorage>;
  manifest: Manifest;
  databasePath: string;
} {
  const storage = workspaceStorage(workspacePath);
  const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as Manifest;
  const databasePath = path.join(
    storage.directory,
    "generations",
    manifest.generationId,
    "index.db",
  );
  return { storage, manifest, databasePath };
}

function writeManifest(storage: ReturnType<typeof workspaceStorage>, manifest: Manifest): void {
  writeFileSync(storage.currentJson, `${JSON.stringify(manifest, null, 2)}\n`);
}

/** Schema version 1 shape written by angular/main: metadata 1 and no symbol_signatures table. */
function rewriteAsSchemaVersion1(workspacePath: string): void {
  const { storage, manifest, databasePath } = readManifest(workspacePath);
  const database = new Database(databasePath);
  try {
    database.prepare("UPDATE metadata SET value = ? WHERE key = ?").run("1", "schemaVersion");
    database.exec("DROP TABLE IF EXISTS symbol_signatures");
  } finally {
    database.close();
  }
  const bytes = readFileSync(databasePath);
  manifest.schemaVersion = 1;
  manifest.artifacts["index.db"] = {
    length: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
  writeManifest(storage, manifest);
}

function setManifestSchemaVersion(workspacePath: string, schemaVersion: unknown): void {
  const { storage, manifest } = readManifest(workspacePath);
  if (schemaVersion === undefined) delete manifest.schemaVersion;
  else manifest.schemaVersion = schemaVersion;
  writeManifest(storage, manifest);
}

function dropSymbolSignatures(workspacePath: string): void {
  const { storage, manifest, databasePath } = readManifest(workspacePath);
  const database = new Database(databasePath);
  try {
    database.exec("DROP TABLE symbol_signatures");
  } finally {
    database.close();
  }
  const bytes = readFileSync(databasePath);
  manifest.artifacts["index.db"] = {
    length: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
  writeManifest(storage, manifest);
}

function corruptIndexBytes(workspacePath: string): void {
  const { databasePath } = readManifest(workspacePath);
  const bytes = readFileSync(databasePath);
  const first = bytes[0];
  if (first === undefined) throw new Error("index.db is empty");
  bytes[0] = first === 0 ? 1 : 0;
  writeFileSync(databasePath, bytes);
}

describe("MCP index schema", () => {
  it("refuses a schema version 1 index from index_status with unsupported_index", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    rewriteAsSchemaVersion1(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("index_status", "unsupported_index", unsupportedMessage(workspace.path)),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns invalid_index from find_symbol when a schema 3 index has no symbol_signatures table", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    dropSymbolSignatures(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope(
          "find_symbol",
          "invalid_index",
          "Snapshot is missing the symbol_signatures table.",
        ),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses schema version 0 with unsupported_index", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    setManifestSchemaVersion(workspace.path, 0);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_symbol", "unsupported_index", unsupportedMessage(workspace.path)),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses a future schema version with unsupported_index", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    setManifestSchemaVersion(workspace.path, 4);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_symbol", "unsupported_index", unsupportedMessage(workspace.path)),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns invalid_index when schemaVersion is missing", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    setManifestSchemaVersion(workspace.path, undefined);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_symbol", "invalid_index", "Invalid snapshot manifest."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns invalid_index when schemaVersion is not a number", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    setManifestSchemaVersion(workspace.path, "2");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_symbol", "invalid_index", "Invalid snapshot manifest."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns invalid_index when index.db does not match the published checksum", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    corruptIndexBytes(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope(
          "find_symbol",
          "invalid_index",
          "Snapshot artifact checksum mismatch: index.db.",
        ),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
