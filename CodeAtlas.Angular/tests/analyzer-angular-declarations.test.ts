import { readFileSync } from "node:fs";
import path from "node:path";
import ts from "typescript";
import { describe, expect, it } from "vitest";
import { indexSourceFile, type IndexedDeclaration } from "../src/symbols.js";
import { packageRoot } from "./support/run-cli.js";

const fixtureRoot = path.join(packageRoot, "tests", "fixtures", "declarations-workspace");

function indexFixture(relativePath: string): IndexedDeclaration[] {
  const text = readFileSync(path.join(fixtureRoot, relativePath), "utf8");
  const sourceFile = ts.createSourceFile(
    relativePath,
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  return indexSourceFile(sourceFile);
}

function memberName(qualifiedName: string): string {
  const dot = qualifiedName.indexOf(".");
  return dot < 0 ? qualifiedName : qualifiedName.slice(dot + 1);
}

describe("Angular declaration analyzer", () => {
  it("signal input(), output() and model() produce input and output symbols equal to their decorator equivalents", () => {
    // Arrange
    const expected = [
      { member: "title", kind: "input", signatures: [] },
      { member: "sku", kind: "input", signatures: [] },
      { member: "saved", kind: "output", signatures: [] },
      { member: "quantity", kind: "input", signatures: [] },
      { member: "quantityChange", kind: "output", signatures: [] },
      { member: "label", kind: "input", signatures: [] },
      { member: "labelChange", kind: "output", signatures: [] },
    ];

    // Act
    const compared = {
      decorator: inputOutputMembers(indexFixture("src/decorator-panel.component.ts")),
      signal: inputOutputMembers(indexFixture("src/signal-panel.component.ts")),
    };

    // Assert
    expect(compared).toEqual({ decorator: expected, signal: expected });
  });

  it("gives directive, pipe and service kinds in standalone and NgModule files", () => {
    // Arrange
    const files = [
      "src/highlight.directive.ts",
      "src/legacy-highlight.directive.ts",
      "src/price.pipe.ts",
      "src/legacy-price.pipe.ts",
      "src/cart.service.ts",
      "src/legacy-cart.service.ts",
    ];
    const expected = [
      { name: "HighlightDirective", kind: "directive" },
      { name: "LegacyHighlightDirective", kind: "directive" },
      { name: "PricePipe", kind: "pipe" },
      { name: "LegacyPricePipe", kind: "pipe" },
      { name: "CartService", kind: "service" },
      { name: "LegacyCartService", kind: "service" },
    ];

    // Act
    const indexed = files.map((file) => {
      const symbol = indexFixture(file)[0];
      return { name: symbol?.qualifiedName, kind: symbol?.kind };
    });

    // Assert
    expect(indexed).toEqual(expected);
  });

  it("functional and class-based guards and interceptors get guard and interceptor kinds", () => {
    // Arrange
    const expected = [
      { name: "AuthGuard", kind: "guard" },
      { name: "AuthGuard.canActivate", kind: "method" },
      { name: "AuthInterceptor", kind: "interceptor" },
      { name: "AuthInterceptor.intercept", kind: "method" },
      { name: "authGuard", kind: "guard" },
      { name: "authInterceptor", kind: "interceptor" },
      { name: "PlainCart", kind: "class" },
      { name: "PlainCart.canActivate", kind: "method" },
      { name: "notAGuard", kind: "function" },
    ];

    // Act
    const indexed = indexFixture("src/auth.ts").map((item) => ({
      name: item.qualifiedName,
      kind: item.kind,
    }));

    // Assert
    expect(indexed).toEqual(expected);
  });
});

function inputOutputMembers(
  declarations: readonly IndexedDeclaration[],
): Array<{ member: string; kind: string; signatures: string[] }> {
  return declarations
    .filter((item) => item.kind === "input" || item.kind === "output")
    .map((item) => ({
      member: memberName(item.qualifiedName),
      kind: item.kind,
      signatures: item.signatures,
    }));
}
