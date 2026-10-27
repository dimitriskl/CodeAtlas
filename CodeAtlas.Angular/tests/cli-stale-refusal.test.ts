import { writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
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

describe("codeatlas-ng coverage stale refusal", () => {
  it("exits 3 when a TypeScript source changed after index", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );

    try {
      // Act
      const result = await runCli(["coverage", "--workspace", workspace.path]);

      // Assert
      expect(result.exitCode).toBe(3);
      expect(result.stdout).toBe("");
      const envelope = JSON.parse(result.stderr) as {
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
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: {
          state: "stale",
          checkedAtUtc: "TIMESTAMP",
          isStale: true,
          changedFiles: ["src/cart.ts"],
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
        error: {
          code: "stale_index",
          message: "The saved index is stale.",
          diagnostics: [],
        },
      });
    } finally {
      await workspace.remove();
    }
  });
});
