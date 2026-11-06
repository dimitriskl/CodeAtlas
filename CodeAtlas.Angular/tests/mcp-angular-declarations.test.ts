import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyDeclarationsWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

/** Symbol kinds from PRD 0001, Identity and schema, in that order. */
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

function parseToolText(result: unknown): {
  success?: boolean;
  coverage?: { symbols: string[] } | null;
  result?: { symbols: FindSymbol[] };
} {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("tool did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("tool did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("tool did not return a text block");
  return JSON.parse(block.text) as {
    success?: boolean;
    coverage?: { symbols: string[] } | null;
    result?: { symbols: FindSymbol[] };
  };
}

function classNamePosition(source: string, className: string): { line: number; column: number } {
  const match = new RegExp(`\\bclass\\s+${className}\\b`).exec(source);
  if (match === null) throw new Error(`fixture has no class ${className}`);
  const nameAt = match.index + match[0].length - className.length;
  const lineStart = source.lastIndexOf("\n", nameAt - 1) + 1;
  return {
    line: source.slice(0, nameAt).split("\n").length,
    column: nameAt - lineStart + 1,
  };
}

describe("Angular declaration MCP tools", () => {
  it("returns standalone and NgModule components for kind component", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();
    const moduleSource = readFileSync(
      path.join(workspace.path, "src/module-cart.component.ts"),
      "utf8",
    );
    const standaloneSource = readFileSync(
      path.join(workspace.path, "src/standalone-cart.component.ts"),
      "utf8",
    );
    const moduleAt = classNamePosition(moduleSource, "Cart");
    const standaloneAt = classNamePosition(standaloneSource, "Cart");

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", kind: "component" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/module-cart.component.ts#Cart",
          kind: "component",
          qualifiedName: "Cart",
          project: "shop",
          path: "src/module-cart.component.ts",
          line: moduleAt.line,
          column: moduleAt.column,
          signatures: [],
        },
        {
          id: "shop::src/standalone-cart.component.ts#Cart",
          kind: "component",
          qualifiedName: "Cart",
          project: "shop",
          path: "src/standalone-cart.component.ts",
          line: standaloneAt.line,
          column: standaloneAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("lists all 16 symbol kinds", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.success).toBe(true);
      expect(envelope.coverage?.symbols).toEqual(symbolKinds);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
