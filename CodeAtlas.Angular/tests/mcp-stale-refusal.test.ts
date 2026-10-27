import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
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

const staleMessage = "The saved index is stale.";

function styleOrAssetWarning(filePath: string): string {
  return `Style or asset changed: ${filePath}.`;
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

function toolEnvelope(result: unknown): Record<string, unknown> {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("coverage did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("coverage did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("coverage did not return a text block");
  return JSON.parse(block.text) as Record<string, unknown>;
}

function generationCoverage(templateCoverage: "complete" | "partial" | "unknown") {
  return {
    language: "TypeScript",
    symbols: symbolKinds,
    references: "static-source-and-template-references",
    referenceSymbolKinds: referenceKinds,
    templateCoverage,
    runtimeCallGraph: false,
    exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    limitations: templateCoverage === "unknown" ? ["no-index"] : [],
  };
}

function freshness(input: {
  state: "fresh" | "stale";
  changedFiles: string[];
  diagnostics: string[];
}) {
  return {
    state: input.state,
    checkedAtUtc: "TIMESTAMP",
    isStale: input.state === "stale",
    changedFiles: input.changedFiles,
    scope: "tracked-input-content-hashes",
    inputCoverage: {
      status: "complete",
      scope: "tracked-input-content-hashes",
      issueCount: 0,
      reasonCodes: [],
    },
    diagnostics: input.diagnostics,
  };
}

async function indexWorkspace(workspacePath: string): Promise<string> {
  const indexed = await runCli(["index", "--workspace", workspacePath]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
  const envelope = JSON.parse(indexed.stdout) as { result: { generationId: string } };
  return envelope.result.generationId;
}

function setBuildOptions(workspacePath: string, options: Record<string, unknown>): void {
  const angularPath = path.join(workspacePath, "angular.json");
  const angular = JSON.parse(readFileSync(angularPath, "utf8")) as {
    projects: { shop: { architect: { build: { options: Record<string, unknown> } } } };
  };
  Object.assign(angular.projects.shop.architect.build.options, options);
  writeFileSync(angularPath, `${JSON.stringify(angular, null, 2)}\n`);
}

describe("MCP freshness and stale refusal", () => {
  it("refuses coverage after a TypeScript source changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({
          state: "stale",
          changedFiles: ["src/cart.ts"],
          diagnostics: [],
        }),
        coverage: generationCoverage("complete"),
        error: {
          code: "stale_index",
          message: staleMessage,
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after package-lock.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const lockPath = path.join(workspace.path, "package-lock.json");
    writeFileSync(lockPath, '{"name":"shop","lockfileVersion":3,"packages":{}}\n');
    await indexWorkspace(workspace.path);
    writeFileSync(lockPath, '{"name":"shop","lockfileVersion":3,"packages":{},"changed":true}\n');
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["package-lock.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after angular.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    const angularPath = path.join(workspace.path, "angular.json");
    const angular = JSON.parse(readFileSync(angularPath, "utf8")) as { version: number };
    angular.version = 2;
    writeFileSync(angularPath, `${JSON.stringify(angular, null, 2)}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["angular.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and warns after a stylesheet changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const stylesheet = path.join(workspace.path, "src", "styles.css");
    writeFileSync(stylesheet, "body { color: black; }\n");
    setBuildOptions(workspace.path, { styles: ["src/styles.css"] });
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "body { color: white; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 1,
        freshness: freshness({
          state: "fresh",
          changedFiles: [],
          diagnostics: [styleOrAssetWarning("src/styles.css")],
        }),
        coverage: generationCoverage("complete"),
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
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and warns after an asset changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const assetDirectory = path.join(workspace.path, "src", "assets");
    mkdirSync(assetDirectory);
    const asset = path.join(assetDirectory, "logo.svg");
    writeFileSync(asset, "<svg></svg>\n");
    setBuildOptions(workspace.path, { assets: ["src/assets"] });
    await indexWorkspace(workspace.path);
    writeFileSync(asset, "<svg><!-- changed --></svg>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("src/assets/logo.svg")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after an external template changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const template = path.join(workspace.path, "src", "cart.html");
    writeFileSync(template, "<p>Cart</p>\n");
    await indexWorkspace(workspace.path);
    writeFileSync(template, "<p>Cart changed</p>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.html"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh after a file is added under node_modules", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const generationId = await indexWorkspace(workspace.path);
    const added = path.join(workspace.path, "node_modules", "added");
    mkdirSync(added, { recursive: true });
    writeFileSync(path.join(added, "index.js"), "module.exports = 1;\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
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
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
