import { existsSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
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

type StatusEnvelope = {
  freshness: { checkedAtUtc: string };
  pagination: { snapshotId: string | null };
  result: {
    generationId: string | null;
    index: string | null;
  };
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

function parseToolText(result: unknown): StatusEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("index_status did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("index_status did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("index_status did not return a text block");
  return JSON.parse(block.text) as StatusEnvelope;
}

describe("MCP index_status", () => {
  it("returns the documented empty result when the workspace has no index", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect({
        ...envelope,
        freshness: { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "index_status",
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
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: null,
          continuationSupported: false,
        },
        result: {
          generationId: null,
          schemaVersion: null,
          contractVersion: 1,
          appVersion: null,
          counts: { projects: 0, files: 0, symbols: 0 },
          index: null,
        },
      });
      expect(existsSync(path.join(workspace.path, ".codeatlas"))).toBe(false);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns the published generation, versions, and counts", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const generationId = (JSON.parse(indexed.stdout) as StatusEnvelope).result.generationId;
    const storage = workspaceStorage(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.freshness.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect({
        ...envelope,
        freshness: { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "index_status",
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
          snapshotId: generationId,
          continuationSupported: false,
        },
        result: {
          generationId,
          schemaVersion: 2,
          contractVersion: 1,
          appVersion: "0.00.01",
          counts: { projects: 1, files: 4, symbols: 1 },
          index: storage.currentJson,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
