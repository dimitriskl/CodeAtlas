import { describe, expect, it } from "vitest";
import { copySymbolsWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

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

type FindSymbol = {
  id: string;
  kind: string;
  qualifiedName: string;
  project: string;
  path: string;
  line: number;
  column: number;
  signatures: string[];
};

type FindEnvelope = {
  freshness: { checkedAtUtc: string } | null;
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
  result?: { symbols: FindSymbol[] };
  error?: { code: string; message: string; diagnostics: string[] };
};

const shopClass: FindSymbol = {
  id: "shop::src/shop.service.ts#shopService",
  kind: "class",
  qualifiedName: "shopService",
  project: "shop",
  path: "src/shop.service.ts",
  line: 1,
  column: 14,
  signatures: [],
};

const shopFunction: FindSymbol = {
  id: "shop::src/lookalikes.ts#shopService",
  kind: "function",
  qualifiedName: "shopService",
  project: "shop",
  path: "src/lookalikes.ts",
  line: 1,
  column: 17,
  signatures: ["function shopService(): void"],
};

const catalogClass: FindSymbol = {
  id: "catalog::projects/catalog/src/shop.service.ts#shopService",
  kind: "class",
  qualifiedName: "shopService",
  project: "catalog",
  path: "projects/catalog/src/shop.service.ts",
  line: 1,
  column: 14,
  signatures: [],
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

function parseToolText(result: unknown): FindEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("find_symbol did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("find_symbol did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("find_symbol did not return a text block");
  return JSON.parse(block.text) as FindEnvelope;
}

function successEnvelope(
  symbols: FindSymbol[],
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
    command: "find_symbol",
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
    result: { symbols },
  };
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

describe("MCP find_symbol", () => {
  it("returns the id, kind, file, line and column of shopService", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopService" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness?.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect(envelope.pagination?.snapshotId).toMatch(/^[0-9a-f]{32}$/);
      expect({
        ...envelope,
        freshness: envelope.freshness ? { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" } : null,
        pagination: envelope.pagination
          ? { ...envelope.pagination, snapshotId: "GENERATION" }
          : undefined,
      }).toEqual(
        successEnvelope([catalogClass, shopFunction, shopClass], {
          offset: 0,
          limit: 100,
          returned: 3,
          total: 3,
          hasMore: false,
          nextOffset: null,
        }),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns one id and every signature of a function overload set", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "parse" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.result?.symbols).toEqual([
        {
          id: "shop::src/parse.ts#parse",
          kind: "function",
          qualifiedName: "parse",
          project: "shop",
          path: "src/parse.ts",
          line: 1,
          column: 17,
          signatures: [
            "function parse(value: string): number",
            "function parse(value: number): string",
            "function parse(value: string | number): string | number",
          ],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("excludes look-alike names when kind and project filters are set", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: {
          workspace: workspace.path,
          name: "shopService",
          kind: "class",
          project: "shop",
        },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(1);
      expect(envelope.result?.symbols).toEqual([shopClass]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns page 2 for the snapshot from page 1", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();
    const page1 = await connection.client.callTool({
      name: "find_symbol",
      arguments: { workspace: workspace.path, name: "shopService", offset: 0, limit: 1 },
    });
    const snapshotId = parseToolText(page1).pagination?.snapshotId;

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: {
          workspace: workspace.path,
          name: "shopService",
          offset: 1,
          limit: 1,
          snapshotId,
        },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness?.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect(envelope.pagination?.snapshotId).toBe(snapshotId);
      expect({
        ...envelope,
        freshness: envelope.freshness ? { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" } : null,
        pagination: envelope.pagination
          ? { ...envelope.pagination, snapshotId: "GENERATION" }
          : undefined,
      }).toEqual(
        successEnvelope([shopFunction], {
          offset: 1,
          limit: 1,
          returned: 1,
          total: 3,
          hasMore: true,
          nextOffset: 2,
        }),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns snapshot_mismatch when page 2 is requested after a re-index", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();
    const page1 = await connection.client.callTool({
      name: "find_symbol",
      arguments: { workspace: workspace.path, name: "shopService", offset: 0, limit: 1 },
    });
    const snapshotId = parseToolText(page1).pagination?.snapshotId;
    const reindexed = await runCli(["index", "--workspace", workspace.path]);
    if (reindexed.exitCode !== 0) throw new Error(reindexed.stderr);

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: {
          workspace: workspace.path,
          name: "shopService",
          offset: 1,
          limit: 1,
          snapshotId,
        },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual({
        contractVersion: 1,
        command: "find_symbol",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: null,
        coverage: null,
        error: {
          code: "snapshot_mismatch",
          message: "The index was replaced. Start a new query at offset zero.",
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a symbol declared in a spec file", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopServiceSpec" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/shop.service.spec.ts#shopServiceSpec",
          kind: "class",
          qualifiedName: "shopServiceSpec",
          project: "shop",
          path: "src/shop.service.spec.ts",
          line: 1,
          column: 14,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a method by its nested qualified name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart.total" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Cart.total",
          kind: "method",
          qualifiedName: "Cart.total",
          project: "shop",
          path: "src/members.ts",
          line: 2,
          column: 3,
          signatures: ["total(): number"],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an interface by name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Line", kind: "interface" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Line",
          kind: "interface",
          qualifiedName: "Line",
          project: "shop",
          path: "src/members.ts",
          line: 9,
          column: 18,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a property by its nested qualified name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart.price", kind: "property" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Cart.price",
          kind: "property",
          qualifiedName: "Cart.price",
          project: "shop",
          path: "src/members.ts",
          line: 6,
          column: 3,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an interface property by its qualified name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Line.quantity" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Line.quantity",
          kind: "property",
          qualifiedName: "Line.quantity",
          project: "shop",
          path: "src/members.ts",
          line: 10,
          column: 3,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a function signature with the parameter list and without comments", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "ping" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/ping.ts#ping",
          kind: "function",
          qualifiedName: "ping",
          project: "shop",
          path: "src/ping.ts",
          line: 1,
          column: 17,
          signatures: ["function ping()"],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an enum by name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CartStatus" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#CartStatus",
          kind: "enum",
          qualifiedName: "CartStatus",
          project: "shop",
          path: "src/members.ts",
          line: 13,
          column: 13,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for an enum member name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Open" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.snapshotId).toMatch(/^[0-9a-f]{32}$/);
      expect({ ...envelope.pagination, snapshotId: "GENERATION" }).toEqual({
        offset: 0,
        limit: 100,
        returned: 0,
        total: 0,
        hasMore: false,
        nextOffset: null,
        snapshotId: "GENERATION",
        continuationSupported: true,
      });
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a type alias by name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CartTotal" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#CartTotal",
          kind: "type-alias",
          qualifiedName: "CartTotal",
          project: "shop",
          path: "src/members.ts",
          line: 17,
          column: 13,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a nested class property by its qualified name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Shop.Item.label" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Shop.Item.label",
          kind: "property",
          qualifiedName: "Shop.Item.label",
          project: "shop",
          path: "src/members.ts",
          line: 21,
          column: 5,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a static method by its qualified name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Factory.<static>.create" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Factory.<static>.create",
          kind: "method",
          qualifiedName: "Factory.<static>.create",
          project: "shop",
          path: "src/members.ts",
          line: 26,
          column: 10,
          signatures: ["static create(): Factory"],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
