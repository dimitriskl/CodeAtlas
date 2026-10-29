import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import Database from "better-sqlite3";
import { describe, expect, it } from "vitest";
import { workspaceStorage } from "../src/workspace-storage.js";
import { copyReferencesWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
import { packageRoot, runCli } from "./support/run-cli.js";

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

type ReferenceHit = {
  targetId: string;
  kind: string;
  project: string;
  path: string;
  line: number;
  column: number;
};

type FindEnvelope = {
  contractVersion?: number;
  command?: string;
  success?: boolean;
  responseToolVersion?: string;
  warningCount?: number;
  freshness: { checkedAtUtc: string } | null;
  coverage?: unknown;
  pagination?: {
    offset: number;
    limit: number;
    returned: number;
    total: number;
    hasMore: boolean;
    nextOffset: number | null;
    snapshotId: string | null;
    continuationSupported: boolean;
  };
  result?: { references: ReferenceHit[] };
  error?: { code: string; message: string; diagnostics: string[] };
};

const cartTotal = "shop::src/cart.ts#Cart.total";
const bagTotal = "shop::src/cart.ts#Bag.total";
const cartClass = "shop::src/cart.ts#Cart";

/** Call sites of `Cart.total`. `Bag.total()` at bill.ts:5:44 is not one of them. */
const cartTotalCalls: ReferenceHit[] = [
  hit(cartTotal, "invocation", "src/bill.ts", 4, 22),
  hit(cartTotal, "invocation", "src/bill.ts", 5, 15),
  hit(cartTotal, "invocation", "src/bill.ts", 5, 30),
  hit(cartTotal, "invocation", "src/cart.ts", 7, 17),
];

/**
 * Shorthand reads of `Cart.total` in src/shorthand.ts.
 * The names introduced by those bindings are not further references.
 */
const shorthandTotals: ReferenceHit[] = [
  hit(cartTotal, "reference", "src/shorthand.ts", 4, 11),
  hit(cartTotal, "reference", "src/shorthand.ts", 8, 24),
  hit(cartTotal, "reference", "src/shorthand.ts", 14, 6),
];

/** Every stored use of `Cart.total`, in path, line, column order. */
const cartTotalUses: ReferenceHit[] = [...cartTotalCalls, ...shorthandTotals];

/** The only call of `Bag.total`. */
const bagTotalCalls: ReferenceHit[] = [hit(bagTotal, "invocation", "src/bill.ts", 5, 44)];

/** Imports and type annotations of `Cart`, plus `new Cart()` as an invocation. */
const cartUses: ReferenceHit[] = [
  hit(cartClass, "reference", "src/bill.ts", 1, 15),
  hit(cartClass, "reference", "src/bill.ts", 3, 28),
  hit(cartClass, "reference", "src/bill.ts", 8, 25),
  hit(cartClass, "invocation", "src/bill.ts", 9, 14),
  hit(cartClass, "reference", "src/label.ts", 1, 10),
  hit(cartClass, "reference", "src/label.ts", 3, 29),
  hit(cartClass, "reference", "src/label.ts", 3, 36),
  hit(cartClass, "reference", "src/shorthand.ts", 1, 10),
  hit(cartClass, "reference", "src/shorthand.ts", 3, 28),
  hit(cartClass, "reference", "src/shorthand.ts", 8, 33),
  hit(cartClass, "reference", "src/shorthand.ts", 12, 30),
];

function hit(
  targetId: string,
  kind: string,
  file: string,
  line: number,
  column: number,
): ReferenceHit {
  return { targetId, kind, project: "shop", path: file, line, column };
}

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

function parseToolText(result: unknown): FindEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("find_references did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("find_references did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("find_references did not return a text block");
  return JSON.parse(block.text) as FindEnvelope;
}

function errorEnvelope(code: string, message: string) {
  return {
    contractVersion: 1,
    command: "find_references",
    success: false,
    responseToolVersion: "0.00.01",
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: {
      code,
      message,
      diagnostics: [] as string[],
    },
  };
}

function successEnvelope(
  references: ReferenceHit[],
  pagination: {
    offset: number;
    limit: number;
    returned: number;
    total: number;
    hasMore: boolean;
    nextOffset: number | null;
  },
) {
  return {
    contractVersion: 1,
    command: "find_references",
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
      ...pagination,
      snapshotId: "GENERATION",
      continuationSupported: true,
    },
    result: { references },
  };
}

function stable(envelope: FindEnvelope) {
  return {
    ...envelope,
    freshness: envelope.freshness ? { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" } : null,
    pagination: envelope.pagination
      ? { ...envelope.pagination, snapshotId: "GENERATION" }
      : undefined,
  };
}

async function indexedReferencesWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  const workspace = await copyReferencesWorkspace();
  const indexed = await runCli(["index", "--workspace", workspace.path]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
  return workspace;
}

/** Previous schema: version 2 stores symbols and signatures, and has no references table. */
function rewriteAsSchemaVersion2(workspacePath: string): void {
  const storage = workspaceStorage(workspacePath);
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
    manifest.generationId,
    "index.db",
  );
  const database = new Database(databasePath);
  try {
    database.prepare("UPDATE metadata SET value = ? WHERE key = ?").run("2", "schemaVersion");
    database.exec('DROP TABLE IF EXISTS "references"');
  } finally {
    database.close();
  }
  const bytes = readFileSync(databasePath);
  manifest.schemaVersion = 2;
  manifest.artifacts["index.db"] = {
    length: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
  writeFileSync(storage.currentJson, `${JSON.stringify(manifest, null, 2)}\n`);
}

describe("MCP find_references", () => {
  it("returns every call site of a method as kind invocation at the call's name", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, kinds: ["invocation"] },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness?.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect(envelope.pagination?.snapshotId).toMatch(/^[0-9a-f]{32}$/);
      expect(stable(envelope)).toEqual(
        successEnvelope(cartTotalCalls, {
          offset: 0,
          limit: 100,
          returned: cartTotalCalls.length,
          total: cartTotalCalls.length,
          hasMore: false,
          nextOffset: null,
        }),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns type annotations and imports as kind reference", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartClass },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(stable(envelope)).toEqual(
        successEnvelope(cartUses, {
          offset: 0,
          limit: 100,
          returned: cartUses.length,
          total: cartUses.length,
          hasMore: false,
          nextOffset: null,
        }),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns only the requested reference kinds", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();
    const referencesOnly = cartUses.filter((use) => use.kind === "reference");

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartClass, kinds: ["reference"] },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(stable(envelope)).toEqual(
        successEnvelope(referencesOnly, {
          offset: 0,
          limit: 100,
          returned: referencesOnly.length,
          total: referencesOnly.length,
          hasMore: false,
          nextOffset: null,
        }),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("does not return a look-alike method from another class", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const cart = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });
      const bag = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: bagTotal },
      });

      // Assert
      expect(cart.isError).toBeFalsy();
      expect(bag.isError).toBeFalsy();
      expect(parseToolText(cart).result?.references).toEqual(cartTotalUses);
      expect(parseToolText(bag).result?.references).toEqual(bagTotalCalls);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("records a shorthand variable declaration as a reference to the property", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();
    const declared = shorthandTotals[0];
    if (declared === undefined) throw new Error("missing shorthand declaration");

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const references = parseToolText(result).result?.references;
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 4),
      ).toEqual([declared]);
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 5),
      ).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("records a shorthand parameter pattern as a reference to the property", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();
    const parameter = shorthandTotals[1];
    if (parameter === undefined) throw new Error("missing shorthand parameter");

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const references = parseToolText(result).result?.references;
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 8),
      ).toEqual([parameter]);
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 9),
      ).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("records a shorthand destructuring assignment as a reference to the property", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();
    const assigned = shorthandTotals[2];
    if (assigned === undefined) throw new Error("missing shorthand assignment");

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const references = parseToolText(result).result?.references;
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 14),
      ).toEqual([assigned]);
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 15),
      ).toEqual([]);
      expect(
        references?.filter((hit) => hit.path === "src/shorthand.ts" && hit.line === 13),
      ).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns the next page from the same snapshot and snapshot_mismatch after a re-index", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();
    const first = cartTotalCalls[0];
    const second = cartTotalCalls[1];
    if (first === undefined || second === undefined) throw new Error("missing call sites");

    try {
      // Act
      const page1Result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, offset: 0, limit: 1 },
      });
      expect(page1Result.isError).toBeFalsy();
      const page1 = parseToolText(page1Result);
      const snapshotId = page1.pagination?.snapshotId;
      expect(snapshotId).toMatch(/^[0-9a-f]{32}$/);
      expect(stable(page1)).toEqual(
        successEnvelope([first], {
          offset: 0,
          limit: 1,
          returned: 1,
          total: cartTotalUses.length,
          hasMore: true,
          nextOffset: 1,
        }),
      );

      const page2Result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, offset: 1, limit: 1, snapshotId },
      });
      expect(page2Result.isError).toBeFalsy();
      const page2 = parseToolText(page2Result);
      expect(page2.pagination?.snapshotId).toBe(snapshotId);
      expect(page2.pagination?.offset).toBe(1);
      expect(page2.pagination?.hasMore).toBe(true);
      expect(page2.result?.references).toEqual([second]);

      const reindexed = await runCli(["index", "--workspace", workspace.path]);
      if (reindexed.exitCode !== 0) throw new Error(reindexed.stderr);
      const mismatch = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, offset: 1, limit: 1, snapshotId },
      });

      // Assert
      expect(mismatch.isError).toBe(true);
      expect(parseToolText(mismatch)).toEqual(
        errorEnvelope(
          "snapshot_mismatch",
          "The index was replaced. Start a new query at offset zero.",
        ),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns symbol_not_found for an unknown symbol id", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: "shop::src/cart.ts#Missing" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("symbol_not_found", "Symbol id was not found."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses a schema version 2 index with unsupported_index and tells the user to re-index", async () => {
    // Arrange
    const workspace = await indexedReferencesWorkspace();
    rewriteAsSchemaVersion2(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope(
          "unsupported_index",
          `Unsupported index schema. Run codeatlas-ng index --workspace ${workspace.path} to rebuild it.`,
        ),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("documents find_references in MCP-CONTRACT.md", () => {
    // Arrange
    const contract = readFileSync(path.join(packageRoot, "docs", "MCP-CONTRACT.md"), "utf8");

    // Act
    const section = contract.includes("## MCP tool `find_references`");

    // Assert
    expect(section).toBe(true);
  });
});
