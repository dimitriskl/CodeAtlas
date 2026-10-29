import ts from "typescript";

const guardInterfaces = new Set([
  "CanActivate",
  "CanActivateChild",
  "CanDeactivate",
  "CanMatch",
  "CanLoad",
]);

const guardFunctions = new Set([
  "CanActivateFn",
  "CanActivateChildFn",
  "CanDeactivateFn",
  "CanMatchFn",
]);

export type ClassSymbolKind =
  "component" | "directive" | "pipe" | "service" | "guard" | "interceptor" | "class";

export type MemberSymbolKind = "input" | "output" | "property";

export type MemberBinding = {
  kind: MemberSymbolKind;
  /** `model()` also contributes an output named `<property>Change`. */
  modelOutput: boolean;
};

/**
 * Class kind from decorator and `implements` names as written.
 * Priority: component, directive, pipe, guard, interceptor, service, class.
 */
export function classDeclarationKind(node: ts.ClassDeclaration): ClassSymbolKind {
  const decorators = decoratorNames(node);
  if (decorators.has("Component")) return "component";
  if (decorators.has("Directive")) return "directive";
  if (decorators.has("Pipe")) return "pipe";
  const implemented = implementedNames(node);
  if (implemented.some((name) => guardInterfaces.has(name))) return "guard";
  if (implemented.includes("HttpInterceptor")) return "interceptor";
  if (decorators.has("Injectable")) return "service";
  return "class";
}

/** `@Input`, `@Output`, or a signal `input()`, `output()`, or `model()` initializer. */
export function propertyBinding(node: ts.PropertyDeclaration): MemberBinding {
  const decorators = decoratorNames(node);
  if (decorators.has("Input")) return { kind: "input", modelOutput: false };
  if (decorators.has("Output")) return { kind: "output", modelOutput: false };
  const signal = signalForm(node.initializer);
  if (signal === "input") return { kind: "input", modelOutput: false };
  if (signal === "output") return { kind: "output", modelOutput: false };
  if (signal === "model") return { kind: "input", modelOutput: true };
  return { kind: "property", modelOutput: false };
}

/** `@Input` or `@Output` on a get or set accessor. */
export function accessorBinding(
  node: ts.GetAccessorDeclaration | ts.SetAccessorDeclaration,
): MemberSymbolKind {
  const decorators = decoratorNames(node);
  if (decorators.has("Input")) return "input";
  if (decorators.has("Output")) return "output";
  return "property";
}

/**
 * A variable whose type annotation is a functional guard or interceptor.
 * Other variables are not symbols.
 */
export function variableDeclarationKind(
  node: ts.VariableDeclaration,
): "guard" | "interceptor" | undefined {
  const name = typeReferenceName(node.type);
  if (name === undefined) return undefined;
  if (guardFunctions.has(name)) return "guard";
  if (name === "HttpInterceptorFn") return "interceptor";
  return undefined;
}

/** Angular's default output name for `model()`: the property name plus `Change`. */
export function modelOutputName(propertyName: string): string {
  return `${propertyName}Change`;
}

function signalForm(
  initializer: ts.Expression | undefined,
): "input" | "output" | "model" | undefined {
  if (initializer === undefined || !ts.isCallExpression(initializer)) return undefined;
  const called = calleeName(initializer.expression);
  if (called === "input" || called === "input.required") return "input";
  if (called === "output") return "output";
  if (called === "model" || called === "model.required") return "model";
  return undefined;
}

function calleeName(expression: ts.Expression): string | undefined {
  if (ts.isIdentifier(expression)) return expression.text;
  if (
    ts.isPropertyAccessExpression(expression) &&
    ts.isIdentifier(expression.expression) &&
    expression.name.text === "required"
  ) {
    return `${expression.expression.text}.required`;
  }
  return undefined;
}

function decoratorNames(node: ts.Node): Set<string> {
  const names = new Set<string>();
  if (!ts.canHaveDecorators(node)) return names;
  for (const decorator of ts.getDecorators(node) ?? []) {
    const name = decoratorName(decorator);
    if (name !== undefined) names.add(name);
  }
  return names;
}

function decoratorName(decorator: ts.Decorator): string | undefined {
  const expression = ts.isCallExpression(decorator.expression)
    ? decorator.expression.expression
    : decorator.expression;
  return expressionName(expression);
}

function implementedNames(node: ts.ClassDeclaration): string[] {
  const names: string[] = [];
  for (const clause of node.heritageClauses ?? []) {
    if (clause.token !== ts.SyntaxKind.ImplementsKeyword) continue;
    for (const type of clause.types) {
      const name = expressionName(type.expression);
      if (name !== undefined) names.push(name);
    }
  }
  return names;
}

function expressionName(node: ts.Node): string | undefined {
  if (ts.isIdentifier(node)) return node.text;
  if (ts.isPropertyAccessExpression(node)) return node.name.text;
  return undefined;
}

function typeReferenceName(type: ts.TypeNode | undefined): string | undefined {
  if (type === undefined || !ts.isTypeReferenceNode(type)) return undefined;
  return ts.isIdentifier(type.typeName) ? type.typeName.text : type.typeName.right.text;
}
