import { describe, expect, it } from "vitest";
import { connectStdio } from "./support/mcp-client.js";
import { readPackageVersion } from "./support/run-cli.js";

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

describe("codeatlas-ng-mcp stdio coverage", () => {
  it("returns the shared envelope with freshness unknown", async () => {
    // Arrange
    const packageVersion = readPackageVersion();
    const connection = await connectStdio();
    const startedAt = Date.now();

    try {
      // Act
      const result = await connection.client.callTool({ name: "coverage", arguments: {} });

      // Assert
      expect(packageVersion).toBe("0.00.01");
      expect(result.isError).toBeFalsy();
      const content: unknown = result.content;
      if (!Array.isArray(content)) {
        throw new Error("coverage did not return content");
      }
      const block: unknown = content[0];
      if (!isTextBlock(block)) {
        throw new Error("coverage did not return a text block");
      }
      const envelope = JSON.parse(block.text) as {
        responseToolVersion: string;
        freshness: { state: string; checkedAtUtc: string; isStale: boolean };
        coverage: { language: string; templateCoverage: string; limitations: string[] };
        contractVersion: number;
        command: string;
        success: boolean;
        warningCount: number;
      };
      const checkedAt = Date.parse(envelope.freshness.checkedAtUtc);
      expect(envelope.contractVersion).toBe(1);
      expect(envelope.command).toBe("coverage");
      expect(envelope.success).toBe(true);
      expect(envelope.responseToolVersion).toBe(packageVersion);
      expect(envelope.warningCount).toBe(0);
      expect(envelope.freshness.state).toBe("unknown");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.coverage.language).toBe("TypeScript");
      expect(envelope.coverage.templateCoverage).toBe("unknown");
      expect(envelope.coverage.limitations).toContain("no-index");
      expect(Number.isNaN(checkedAt)).toBe(false);
      expect(Math.abs(checkedAt - startedAt)).toBeLessThan(60_000);
    } finally {
      await connection.close();
    }
  });
});
