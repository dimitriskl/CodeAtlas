import ts from "typescript";
import { describe, expect, it } from "vitest";
import { indexSourceFile, type IndexedDeclaration } from "../src/symbols.js";

type NamedKind = { qualifiedName: string; kind: string };

function indexText(text: string): IndexedDeclaration[] {
  const sourceFile = ts.createSourceFile(
    "edges.ts",
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  return indexSourceFile(sourceFile);
}

function namedKinds(declarations: readonly IndexedDeclaration[]): NamedKind[] {
  return declarations.map((item) => ({ qualifiedName: item.qualifiedName, kind: item.kind }));
}

function tokenPosition(source: string, token: string): { line: number; column: number } {
  const match = new RegExp(`\\b${token}\\b`).exec(source);
  if (match === null) throw new Error(`source has no ${token}`);
  const nameAt = match.index;
  const lineStart = source.lastIndexOf("\n", nameAt - 1) + 1;
  return {
    line: source.slice(0, nameAt).split("\n").length,
    column: nameAt - lineStart + 1,
  };
}

describe("Angular declaration edges", () => {
  it("recognizes qualified signal calls by their last identifier, including model() Change outputs", () => {
    // Arrange
    const source = [
      'import * as core from "@angular/core";',
      'import { Component } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      '  readonly title = core.input("");',
      "  readonly sku = core.input.required<string>();",
      "  readonly saved = core.output<void>();",
      "  readonly quantity = core.model(0);",
      "  readonly label = core.model.required<string>();",
      "}",
      "",
    ].join("\n");
    const panelAt = tokenPosition(source, "Panel");
    const titleAt = tokenPosition(source, "title");
    const skuAt = tokenPosition(source, "sku");
    const savedAt = tokenPosition(source, "saved");
    const quantityAt = tokenPosition(source, "quantity");
    const labelAt = tokenPosition(source, "label");

    // Act
    const indexed = indexText(source).map((item) => ({
      qualifiedName: item.qualifiedName,
      kind: item.kind,
      line: item.line,
      column: item.column,
    }));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "Panel", kind: "component", line: panelAt.line, column: panelAt.column },
      { qualifiedName: "Panel.title", kind: "input", line: titleAt.line, column: titleAt.column },
      { qualifiedName: "Panel.sku", kind: "input", line: skuAt.line, column: skuAt.column },
      { qualifiedName: "Panel.saved", kind: "output", line: savedAt.line, column: savedAt.column },
      {
        qualifiedName: "Panel.quantity",
        kind: "input",
        line: quantityAt.line,
        column: quantityAt.column,
      },
      {
        qualifiedName: "Panel.quantityChange",
        kind: "output",
        line: quantityAt.line,
        column: quantityAt.column,
      },
      { qualifiedName: "Panel.label", kind: "input", line: labelAt.line, column: labelAt.column },
      {
        qualifiedName: "Panel.labelChange",
        kind: "output",
        line: labelAt.line,
        column: labelAt.column,
      },
    ]);
  });

  it("does not treat an import alias of input(), output(), or model() as a signal form", () => {
    // Arrange
    const source = [
      'import { Component, input as inFn, model as modelFn, output as outFn } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      '  readonly title = inFn("");',
      "  readonly saved = outFn<void>();",
      "  readonly quantity = modelFn(0);",
      "}",
      "",
    ].join("\n");

    // Act
    const indexed = namedKinds(indexText(source));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "Panel", kind: "component" },
      { qualifiedName: "Panel.title", kind: "property" },
      { qualifiedName: "Panel.saved", kind: "property" },
      { qualifiedName: "Panel.quantity", kind: "property" },
    ]);
  });

  it("places each model() Change output on the property name, including a static member", () => {
    // Arrange
    const source = [
      'import { Component, model } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      "  readonly label = model.required<string>();",
      "  static quantity = model(0);",
      "}",
      "",
    ].join("\n");
    const labelAt = tokenPosition(source, "label");
    const quantityAt = tokenPosition(source, "quantity");

    // Act
    const indexed = indexText(source);

    // Assert
    expect(indexed).toEqual([
      expect.objectContaining({ qualifiedName: "Panel", kind: "component" }),
      expect.objectContaining({
        qualifiedName: "Panel.label",
        kind: "input",
        line: labelAt.line,
        column: labelAt.column,
        signatures: [],
      }),
      expect.objectContaining({
        qualifiedName: "Panel.labelChange",
        kind: "output",
        line: labelAt.line,
        column: labelAt.column,
        signatures: [],
      }),
      expect.objectContaining({
        qualifiedName: "Panel.<static>.quantity",
        kind: "input",
        line: quantityAt.line,
        column: quantityAt.column,
        signatures: [],
      }),
      expect.objectContaining({
        qualifiedName: "Panel.<static>.quantityChange",
        kind: "output",
        line: quantityAt.line,
        column: quantityAt.column,
        signatures: [],
      }),
    ]);
  });

  it("keeps @Input and @Output aliases as input and output under the property name", () => {
    // Arrange
    const source = [
      'import { Component, EventEmitter, Input, Output } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      '  @Input("heading") title = "";',
      '  @Input({ alias: "skuCode", required: true }) sku!: string;',
      '  @Output("savedEvent") saved = new EventEmitter<void>();',
      '  @Output({ alias: "qtyChange" }) quantityChange = new EventEmitter<number>();',
      '  @ng.Input() qualified = "";',
      "  @ng.Output() qualifiedSaved = new EventEmitter<void>();",
      "}",
      "",
    ].join("\n");

    // Act
    const indexed = namedKinds(indexText(source));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "Panel", kind: "component" },
      { qualifiedName: "Panel.title", kind: "input" },
      { qualifiedName: "Panel.sku", kind: "input" },
      { qualifiedName: "Panel.saved", kind: "output" },
      { qualifiedName: "Panel.quantityChange", kind: "output" },
      { qualifiedName: "Panel.qualified", kind: "input" },
      { qualifiedName: "Panel.qualifiedSaved", kind: "output" },
    ]);
  });

  it("lets an @Input or @Output decorator win over a model() initializer", () => {
    // Arrange
    const source = [
      'import { Component, Input, Output, model } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      '  @Input() title = model("");',
      "  @Output() saved = model(0);",
      "}",
      "",
    ].join("\n");

    // Act
    const indexed = namedKinds(indexText(source));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "Panel", kind: "component" },
      { qualifiedName: "Panel.title", kind: "input" },
      { qualifiedName: "Panel.saved", kind: "output" },
    ]);
  });

  it("classifies accessor @Input and @Output, with input winning when both are present", () => {
    // Arrange
    const source = [
      'import { Component, Input, Output } from "@angular/core";',
      "",
      "@Component({",
      '  selector: "app-panel",',
      '  template: "",',
      "})",
      "export class Panel {",
      "  @Input()",
      "  get title(): string {",
      '    return "";',
      "  }",
      "  set title(value: string) {}",
      "",
      "  get saved(): number {",
      "    return 1;",
      "  }",
      "  @Output()",
      "  set saved(value: number) {}",
      "",
      "  @Input()",
      "  get sku(): string {",
      '    return "";',
      "  }",
      "  @Output()",
      "  set sku(value: string) {}",
      "}",
      "",
    ].join("\n");

    // Act
    const indexed = namedKinds(indexText(source));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "Panel", kind: "component" },
      { qualifiedName: "Panel.title", kind: "input" },
      { qualifiedName: "Panel.saved", kind: "output" },
      { qualifiedName: "Panel.sku", kind: "input" },
    ]);
  });

  it("recognizes every guard variant and keeps priority, aliases, and NgModule classes", () => {
    // Arrange
    const source = [
      'import { Component as Comp, Component, Directive, Injectable, NgModule, Pipe } from "@angular/core";',
      'import { CanActivateChild, CanDeactivate, CanLoad, CanMatch } from "@angular/router";',
      'import { CanActivateChildFn, CanDeactivateFn, CanMatchFn } from "@angular/router";',
      "",
      "@Injectable()",
      "export class ChildGuard implements CanActivateChild {",
      "  canActivateChild(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      "@Injectable()",
      "export class LeaveGuard implements CanDeactivate<unknown> {",
      "  canDeactivate(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      "@Injectable()",
      "export class MatchGuard implements CanMatch {",
      "  canMatch(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      "@Injectable()",
      "export class LoadGuard implements CanLoad {",
      "  canLoad(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      "export class AliasGuard implements GuardLike {}",
      "type GuardLike = CanActivateChild;",
      "",
      "export const childGuard: CanActivateChildFn = () => true;",
      "export const leaveGuard: CanDeactivateFn<unknown> = () => true;",
      "export const matchGuard: CanMatchFn = () => true;",
      "export const notALoadGuard: CanLoadFn = () => true;",
      "export const qualifiedChild: router.CanActivateChildFn = () => true;",
      "",
      "export class QualifiedChild implements router.CanActivateChild {",
      "  canActivateChild(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      '@Comp({ selector: "app-aliased", template: "" })',
      "export class Aliased {}",
      "",
      '@ng.Component({ selector: "app-qualified", template: "" })',
      "export class QualifiedComponent {}",
      "",
      "@Injectable()",
      '@Component({ selector: "app-cart", standalone: false, template: "" })',
      "export class Cart implements CanActivateChild {",
      "  canActivateChild(): boolean {",
      "    return true;",
      "  }",
      "}",
      "",
      '@Pipe({ name: "price" })',
      '@Directive({ selector: "[appPrice]" })',
      "export class Price {}",
      "",
      "@NgModule({ declarations: [Cart, Price] })",
      "export class ShopModule {}",
      "",
    ].join("\n");

    // Act
    const indexed = namedKinds(indexText(source));

    // Assert
    expect(indexed).toEqual([
      { qualifiedName: "ChildGuard", kind: "guard" },
      { qualifiedName: "ChildGuard.canActivateChild", kind: "method" },
      { qualifiedName: "LeaveGuard", kind: "guard" },
      { qualifiedName: "LeaveGuard.canDeactivate", kind: "method" },
      { qualifiedName: "MatchGuard", kind: "guard" },
      { qualifiedName: "MatchGuard.canMatch", kind: "method" },
      { qualifiedName: "LoadGuard", kind: "guard" },
      { qualifiedName: "LoadGuard.canLoad", kind: "method" },
      { qualifiedName: "AliasGuard", kind: "class" },
      { qualifiedName: "GuardLike", kind: "type-alias" },
      { qualifiedName: "childGuard", kind: "guard" },
      { qualifiedName: "leaveGuard", kind: "guard" },
      { qualifiedName: "matchGuard", kind: "guard" },
      { qualifiedName: "qualifiedChild", kind: "guard" },
      { qualifiedName: "QualifiedChild", kind: "guard" },
      { qualifiedName: "QualifiedChild.canActivateChild", kind: "method" },
      { qualifiedName: "Aliased", kind: "class" },
      { qualifiedName: "QualifiedComponent", kind: "component" },
      { qualifiedName: "Cart", kind: "component" },
      { qualifiedName: "Cart.canActivateChild", kind: "method" },
      { qualifiedName: "Price", kind: "directive" },
      { qualifiedName: "ShopModule", kind: "class" },
    ]);
  });
});
