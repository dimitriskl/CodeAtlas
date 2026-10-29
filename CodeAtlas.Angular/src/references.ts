import ts from "typescript";

export type IndexedReference = {
  targetId: string;
  kind: "invocation" | "reference";
  project: string;
  path: string;
  line: number;
  column: number;
};

/**
 * Uses of indexed symbols in one source file.
 * A call, `new`, or tagged template is `invocation`. Every other use is `reference`.
 * The declaration's own name is not a use. Element access is not a use.
 */
export function referencesInFile(
  checker: ts.TypeChecker,
  sourceFile: ts.SourceFile,
  relativePath: string,
  project: string,
  idsByPosition: ReadonlyMap<string, string>,
  relativeOf: ReadonlyMap<string, string>,
): IndexedReference[] {
  const found: IndexedReference[] = [];
  const seen = new Set<string>();
  const visit = (node: ts.Node): void => {
    if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node)) {
      recordUse(node);
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
  return found;

  function recordUse(node: ts.Identifier | ts.PrivateIdentifier): void {
    const symbol = resolveSymbol(checker, node);
    if (symbol === undefined || isDeclarationName(node, symbol)) return;
    const kind = isInvocation(node) ? "invocation" : "reference";
    const position = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile, false));
    const line = position.line + 1;
    const column = position.character + 1;
    for (const targetId of targetIds(symbol, idsByPosition, relativeOf)) {
      const key = `${targetId}\0${kind}\0${line}\0${column}`;
      if (seen.has(key)) continue;
      seen.add(key);
      found.push({ targetId, kind, project, path: relativePath, line, column });
    }
  }
}

function resolveSymbol(checker: ts.TypeChecker, node: ts.Node): ts.Symbol | undefined {
  const symbol = checker.getSymbolAtLocation(node);
  if (symbol === undefined) return undefined;
  if ((symbol.flags & ts.SymbolFlags.Alias) === 0) return symbol;
  return checker.getAliasedSymbol(symbol);
}

function isDeclarationName(node: ts.Node, symbol: ts.Symbol): boolean {
  for (const declaration of symbol.declarations ?? []) {
    if (declarationName(declaration) === node) return true;
  }
  return false;
}

function declarationName(declaration: ts.Declaration): ts.Node | undefined {
  if (ts.isParameter(declaration)) return identifierName(declaration.name);
  if (
    ts.isClassDeclaration(declaration) ||
    ts.isInterfaceDeclaration(declaration) ||
    ts.isTypeAliasDeclaration(declaration) ||
    ts.isEnumDeclaration(declaration) ||
    ts.isFunctionDeclaration(declaration) ||
    ts.isMethodDeclaration(declaration) ||
    ts.isMethodSignature(declaration) ||
    ts.isPropertyDeclaration(declaration) ||
    ts.isPropertySignature(declaration) ||
    ts.isGetAccessor(declaration) ||
    ts.isSetAccessor(declaration) ||
    ts.isModuleDeclaration(declaration)
  ) {
    return declaration.name === undefined ? undefined : identifierName(declaration.name);
  }
  return undefined;
}

function identifierName(name: ts.Node): ts.Identifier | ts.PrivateIdentifier | undefined {
  if (ts.isIdentifier(name) || ts.isPrivateIdentifier(name)) return name;
  return undefined;
}

function targetIds(
  symbol: ts.Symbol,
  idsByPosition: ReadonlyMap<string, string>,
  relativeOf: ReadonlyMap<string, string>,
): string[] {
  const ids: string[] = [];
  for (const declaration of symbol.declarations ?? []) {
    const name = declarationName(declaration);
    if (name === undefined) continue;
    const file = name.getSourceFile();
    const relative = relativeOf.get(file.fileName);
    if (relative === undefined) continue;
    const position = file.getLineAndCharacterOfPosition(name.getStart(file, false));
    const id = idsByPosition.get(`${relative}:${position.line + 1}:${position.character + 1}`);
    if (id === undefined || ids.includes(id)) continue;
    ids.push(id);
  }
  return ids;
}

function isInvocation(node: ts.Node): boolean {
  const parent = node.parent;
  if (ts.isPropertyAccessExpression(parent) && parent.name === node) return isCallee(parent);
  return isCallee(node);
}

function isCallee(expression: ts.Node): boolean {
  let current = expression;
  for (;;) {
    const parent = current.parent;
    if (parent === undefined) return false;
    const callee = calledExpression(parent);
    if (callee === current) return true;
    if (
      (ts.isParenthesizedExpression(parent) ||
        ts.isNonNullExpression(parent) ||
        ts.isAsExpression(parent) ||
        ts.isTypeAssertionExpression(parent) ||
        ts.isSatisfiesExpression(parent)) &&
      parent.expression === current
    ) {
      current = parent;
      continue;
    }
    return false;
  }
}

function calledExpression(node: ts.Node): ts.Expression | undefined {
  if (ts.isCallExpression(node) || ts.isNewExpression(node)) return node.expression;
  if (ts.isTaggedTemplateExpression(node)) return node.tag;
  return undefined;
}
