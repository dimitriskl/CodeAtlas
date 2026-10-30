import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyDeclarationsWorkspace } from "./support/fixture.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

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

type ToolEnvelope = {
  success?: boolean;
  freshness?: { state?: string } | null;
  result?: { schemaVersion?: number; counts?: { symbols?: number }; symbols?: FindSymbol[] };
  coverage?: { templateCoverage?: string; symbols?: string[] } | null;
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

function parseToolText(result: unknown): ToolEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("tool did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("tool did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("tool did not return a text block");
  return JSON.parse(block.text) as ToolEnvelope;
}

function tokenPosition(source: string, token: string): { line: number; column: number } {
  const match = new RegExp(`\\b${token}\\b`).exec(source);
  if (match === null) throw new Error(`fixture has no ${token}`);
  const nameAt = match.index;
  const lineStart = source.lastIndexOf("\n", nameAt - 1) + 1;
  return {
    line: source.slice(0, nameAt).split("\n").length,
    column: nameAt - lineStart + 1,
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

async function indexedDeclarationsWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  const workspace = await copyDeclarationsWorkspace();
  const indexed = await runCli(["index", "--workspace", workspace.path]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
  return workspace;
}

describe("Angular declaration query edges", () => {
  it("publishes schema version 3 and the model() Change outputs for the declarations workspace", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();

    try {
      // Act
      const indexed = await runCli(["index", "--workspace", workspace.path]);

      // Assert
      expect(indexed.exitCode).toBe(0);
      expect(indexed.stderr).toBe("");
      const envelope = JSON.parse(indexed.stdout) as ToolEnvelope;
      expect(envelope.success).toBe(true);
      expect(envelope.freshness?.state).toBe("fresh");
      expect(envelope.result?.schemaVersion).toBe(3);
      expect(envelope.coverage?.templateCoverage).toBe("complete");
      expect(envelope.coverage?.symbols).toEqual([
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
      ]);
      expect(envelope.result?.counts?.symbols).toBe(35);
    } finally {
      await workspace.remove();
    }
  });

  it("returns decorator and signal inputs for kind input", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const decoratorSource = readFileSync(
      path.join(workspace.path, "src/decorator-panel.component.ts"),
      "utf8",
    );
    const signalSource = readFileSync(
      path.join(workspace.path, "src/signal-panel.component.ts"),
      "utf8",
    );
    const decoratorAt = tokenPosition(decoratorSource, "title");
    const signalAt = tokenPosition(signalSource, "title");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "title", kind: "input" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/decorator-panel.component.ts#DecoratorPanel.title",
          kind: "input",
          qualifiedName: "DecoratorPanel.title",
          project: "shop",
          path: "src/decorator-panel.component.ts",
          line: decoratorAt.line,
          column: decoratorAt.column,
          signatures: [],
        },
        {
          id: "shop::src/signal-panel.component.ts#SignalPanel.title",
          kind: "input",
          qualifiedName: "SignalPanel.title",
          project: "shop",
          path: "src/signal-panel.component.ts",
          line: signalAt.line,
          column: signalAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns the model() Change output at the property name and the decorator output", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const decoratorSource = readFileSync(
      path.join(workspace.path, "src/decorator-panel.component.ts"),
      "utf8",
    );
    const signalSource = readFileSync(
      path.join(workspace.path, "src/signal-panel.component.ts"),
      "utf8",
    );
    const decoratorAt = tokenPosition(decoratorSource, "quantityChange");
    const signalAt = tokenPosition(signalSource, "quantity");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "quantityChange", kind: "output" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/decorator-panel.component.ts#DecoratorPanel.quantityChange",
          kind: "output",
          qualifiedName: "DecoratorPanel.quantityChange",
          project: "shop",
          path: "src/decorator-panel.component.ts",
          line: decoratorAt.line,
          column: decoratorAt.column,
          signatures: [],
        },
        {
          id: "shop::src/signal-panel.component.ts#SignalPanel.quantityChange",
          kind: "output",
          qualifiedName: "SignalPanel.quantityChange",
          project: "shop",
          path: "src/signal-panel.component.ts",
          line: signalAt.line,
          column: signalAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an NgModule directive from find_symbol", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(
      path.join(workspace.path, "src/legacy-highlight.directive.ts"),
      "utf8",
    );
    const at = classNamePosition(source, "LegacyHighlightDirective");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: {
          workspace: workspace.path,
          name: "LegacyHighlightDirective",
          kind: "directive",
        },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/legacy-highlight.directive.ts#LegacyHighlightDirective",
          kind: "directive",
          qualifiedName: "LegacyHighlightDirective",
          project: "shop",
          path: "src/legacy-highlight.directive.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a standalone pipe from find_symbol", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/price.pipe.ts"), "utf8");
    const at = classNamePosition(source, "PricePipe");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "PricePipe", kind: "pipe" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/price.pipe.ts#PricePipe",
          kind: "pipe",
          qualifiedName: "PricePipe",
          project: "shop",
          path: "src/price.pipe.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a service from find_symbol", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/cart.service.ts"), "utf8");
    const at = classNamePosition(source, "CartService");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "CartService", kind: "service" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/cart.service.ts#CartService",
          kind: "service",
          qualifiedName: "CartService",
          project: "shop",
          path: "src/cart.service.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a class guard from find_symbol", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/auth.ts"), "utf8");
    const at = classNamePosition(source, "AuthGuard");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "AuthGuard", kind: "guard" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/auth.ts#AuthGuard",
          kind: "guard",
          qualifiedName: "AuthGuard",
          project: "shop",
          path: "src/auth.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a functional interceptor from find_symbol", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/auth.ts"), "utf8");
    const at = tokenPosition(source, "authInterceptor");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "authInterceptor", kind: "interceptor" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/auth.ts#authInterceptor",
          kind: "interceptor",
          qualifiedName: "authInterceptor",
          project: "shop",
          path: "src/auth.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("does not return an @Injectable guard when the kind filter is service", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "AuthGuard", kind: "service" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.success).toBe(true);
      expect(envelope.freshness?.state).toBe("fresh");
      expect(envelope.result?.symbols).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns the plain Cart class and not the components when kind is class", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/cart.ts"), "utf8");
    const at = classNamePosition(source, "Cart");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart", kind: "class" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/cart.ts#Cart",
          kind: "class",
          qualifiedName: "Cart",
          project: "shop",
          path: "src/cart.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns an NgModule class as class", async () => {
    // Arrange
    const workspace = await indexedDeclarationsWorkspace();
    const source = readFileSync(path.join(workspace.path, "src/shop.module.ts"), "utf8");
    const at = classNamePosition(source, "ShopModule");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "ShopModule", kind: "class" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/shop.module.ts#ShopModule",
          kind: "class",
          qualifiedName: "ShopModule",
          project: "shop",
          path: "src/shop.module.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a CanActivateChild class guard from find_symbol", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    const relative = "src/child.guard.ts";
    const source = [
      'import { Injectable } from "@angular/core";',
      'import { CanActivateChild, CanActivateChildFn } from "@angular/router";',
      "",
      "@Injectable()",
      "export class ChildGuard implements CanActivateChild {",
      "  canActivateChild(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      "export const childGuard: CanActivateChildFn = () => true;",
      "",
    ].join("\n");
    writeFileSync(path.join(workspace.path, relative), source);
    const classAt = classNamePosition(source, "ChildGuard");
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "ChildGuard", kind: "guard" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/child.guard.ts#ChildGuard",
          kind: "guard",
          qualifiedName: "ChildGuard",
          project: "shop",
          path: "src/child.guard.ts",
          line: classAt.line,
          column: classAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a CanActivateChildFn guard from find_symbol", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    const source = [
      'import { CanActivateChildFn } from "@angular/router";',
      "",
      "export const childGuard: CanActivateChildFn = () => true;",
      "",
    ].join("\n");
    writeFileSync(path.join(workspace.path, "src/child.guard.ts"), source);
    const functionAt = tokenPosition(source, "childGuard");
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "childGuard", kind: "guard" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/child.guard.ts#childGuard",
          kind: "guard",
          qualifiedName: "childGuard",
          project: "shop",
          path: "src/child.guard.ts",
          line: functionAt.line,
          column: functionAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("indexes a component in another file when one file has a syntax error", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    writeFileSync(
      path.join(workspace.path, "src/broken.ts"),
      "export class StillHere {}\nconst =\n",
    );
    const visible = [
      'import { Component } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-visible",',
      '  template: "",',
      "})",
      "export class Visible {}",
      "",
    ].join("\n");
    writeFileSync(path.join(workspace.path, "src/visible.component.ts"), visible);
    const visibleAt = classNamePosition(visible, "Visible");
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Visible", kind: "component" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/visible.component.ts#Visible",
          kind: "component",
          qualifiedName: "Visible",
          project: "shop",
          path: "src/visible.component.ts",
          line: visibleAt.line,
          column: visibleAt.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns a class declared before a syntax error in the same file", async () => {
    // Arrange
    const workspace = await copyDeclarationsWorkspace();
    const broken = "export class StillHere {}\nconst =\n";
    writeFileSync(path.join(workspace.path, "src/broken.ts"), broken);
    const at = classNamePosition(broken, "StillHere");
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "StillHere", kind: "class" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      expect(parseToolText(result).result?.symbols).toEqual([
        {
          id: "shop::src/broken.ts#StillHere",
          kind: "class",
          qualifiedName: "StillHere",
          project: "shop",
          path: "src/broken.ts",
          line: at.line,
          column: at.column,
          signatures: [],
        },
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
