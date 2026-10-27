import { describe, expect, it } from "vitest";
import { connectInProcess } from "./support/mcp-client.js";

const staticCoverage = {
  language: "TypeScript",
  symbols: [
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
  ],
  references: "static-source-and-template-references",
  referenceSymbolKinds: [
    "invocation",
    "template-call",
    "template-usage",
    "injection",
    "route",
    "reference",
  ],
  templateCoverage: "unknown",
  runtimeCallGraph: false,
  exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
  limitations: ["no-index"],
};

describe("MCP coverage tool", () => {
  it("lists exactly the tool coverage", async () => {
    // Arrange
    const connection = await connectInProcess();

    try {
      // Act
      const listed = await connection.client.listTools();

      // Assert
      expect(listed.tools.map((tool) => tool.name)).toEqual(["coverage"]);
    } finally {
      await connection.close();
    }
  });

  it("returns the shared envelope with freshness unknown and static coverage", async () => {
    // Arrange
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({ name: "coverage", arguments: {} });

      // Assert
      expect(result.isError).toBeFalsy();
      const block = result.content[0];
      expect(block).toMatchObject({ type: "text" });
      if (block === undefined || block.type !== "text") {
        throw new Error("coverage did not return a text block");
      }
      const envelope = JSON.parse(block.text) as {
        freshness: { checkedAtUtc: string };
      };
      expect(envelope.freshness.checkedAtUtc).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
      expect({
        ...envelope,
        freshness: { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
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
        coverage: staticCoverage,
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
      });
    } finally {
      await connection.close();
    }
  });
});
