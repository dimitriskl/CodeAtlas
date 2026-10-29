import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace, copySymbolsWorkspace } from "./support/fixture.js";
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

function errorEnvelope(code: string, message: string) {
  return {
    contractVersion: 1,
    command: "find_symbol",
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

  it("returns an empty page when the name is not in the index", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "doesNotExist" },
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

  it("does not match a name that differs only by case", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "ShopService" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns the class Cart and not its members", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/members.ts#Cart",
          kind: "class",
          qualifiedName: "Cart",
          project: "shop",
          path: "src/members.ts",
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

  it("returns every method whose simple name is total", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "total" },
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

  it("returns a static method by its simple name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "create" },
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

  it("does not match a static method when the static segment is omitted", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Factory.create" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("rejects an empty name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("invalid_arguments", "name must not be empty."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("does not trim a name before matching", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: " shopService" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for a whitespace name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: " " },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("rejects a missing name argument", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const content = result.content;
      if (!Array.isArray(content)) throw new Error("find_symbol did not return content");
      const block: unknown = content[0];
      if (!isTextBlock(block)) throw new Error("find_symbol did not return a text block");
      expect(block.text).toBe(
        "MCP error -32602: Input validation error: Invalid arguments for tool find_symbol: Required at name",
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for a very long name", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Q".repeat(8000) },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.pagination?.continuationSupported).toBe(true);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for a name containing special characters", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "a/b<script>" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a private identifier with its hash", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CaseBox.#hidden" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/qualified.ts#CaseBox.#hidden",
          kind: "property",
          qualifiedName: "CaseBox.#hidden",
          project: "shop",
          path: "src/qualified.ts",
          line: 2,
          column: 3,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns one property for a get and a set", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CaseBox.label" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/qualified.ts#CaseBox.label",
          kind: "property",
          qualifiedName: "CaseBox.label",
          project: "shop",
          path: "src/qualified.ts",
          line: 4,
          column: 7,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a constructor parameter property", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CaseBox.owner" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/qualified.ts#CaseBox.owner",
          kind: "property",
          qualifiedName: "CaseBox.owner",
          project: "shop",
          path: "src/qualified.ts",
          line: 10,
          column: 31,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an interface method and its signature", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "OnlyIface.ack" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/qualified.ts#OnlyIface.ack",
          kind: "method",
          qualifiedName: "OnlyIface.ack",
          project: "shop",
          path: "src/qualified.ts",
          line: 14,
          column: 3,
          signatures: ["ack(): void"],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("rejects a limit above 1000", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopService", limit: 1001 },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("invalid_arguments", "limit must be an integer from 1 through 1000."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("accepts the maximum limit and keeps id order", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopService", limit: 1000 },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination).toMatchObject({
        offset: 0,
        limit: 1000,
        returned: 3,
        total: 3,
        hasMore: false,
        nextOffset: null,
        continuationSupported: true,
      });
      expect(envelope.result?.symbols.map((symbol) => symbol.id)).toEqual([
        catalogClass.id,
        shopFunction.id,
        shopClass.id,
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("rejects an offset greater than zero without snapshotId", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopService", offset: 1 },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("invalid_arguments", "offset greater than zero requires snapshotId."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page when offset is past the last match", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();
    const page1 = await connection.client.callTool({
      name: "find_symbol",
      arguments: { workspace: workspace.path, name: "shopService", limit: 1 },
    });
    const snapshotId = parseToolText(page1).pagination?.snapshotId;

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: {
          workspace: workspace.path,
          name: "shopService",
          offset: 9,
          limit: 1,
          snapshotId,
        },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination).toEqual({
        offset: 9,
        limit: 1,
        returned: 0,
        total: 3,
        hasMore: false,
        nextOffset: null,
        snapshotId,
        continuationSupported: true,
      });
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for an unknown project", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "shopService", project: "missing" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.pagination?.continuationSupported).toBe(true);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("rejects an unknown symbol kind", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", kind: "Class" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("invalid_arguments", "Unknown symbol kind."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an empty page for a component kind filter", async () => {
    // Arrange
    const workspace = await indexedSymbolsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", kind: "component" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.pagination?.total).toBe(0);
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns snapshot_mismatch when snapshotId is not the published generation", async () => {
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
          offset: 0,
          snapshotId: "0".repeat(32),
        },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
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

  it("returns the no-index envelope when the workspace has no generation", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", offset: 0, limit: 7 },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness?.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect({
        ...envelope,
        freshness: envelope.freshness ? { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" } : null,
      }).toEqual({
        contractVersion: 1,
        command: "find_symbol",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: {
          state: "unknown",
          checkedAtUtc: "TIMESTAMP",
          isStale: false,
          changedFiles: [],
          scope: "tracked-input-content-hashes",
          inputCoverage: {
            status: "unknown",
            scope: "no-index",
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
          templateCoverage: "unknown",
          runtimeCallGraph: false,
          exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
          limitations: ["no-index"],
        },
        pagination: {
          offset: 0,
          limit: 7,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: null,
          continuationSupported: false,
        },
        result: { symbols: [] },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns snapshot_mismatch when snapshotId is supplied and no generation is published", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", snapshotId: "abc" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
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
});
