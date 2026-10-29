import ts from "typescript";

export type SourceSymbolKind =
  "class" | "interface" | "function" | "method" | "property" | "enum" | "type-alias";

export type IndexedDeclaration = {
  qualifiedName: string;
  kind: SourceSymbolKind;
  line: number;
  column: number;
  signatures: string[];
};

type FoundSymbol = {
  qualifiedName: string;
  kind: SourceSymbolKind;
  line: number;
  column: number;
  signature: string | null;
};

const kindRank: Record<SourceSymbolKind, number> = {
  class: 0,
  interface: 1,
  enum: 2,
  function: 3,
  "type-alias": 4,
  method: 5,
  property: 6,
};

const skippedModifiers = new Set<ts.SyntaxKind>([
  ts.SyntaxKind.ExportKeyword,
  ts.SyntaxKind.DefaultKeyword,
]);

/** Declarations in one source file. Overloads and merged names are one symbol. */
export function indexSourceFile(sourceFile: ts.SourceFile): IndexedDeclaration[] {
  const found: FoundSymbol[] = [];
  visitList(sourceFile.statements, [], sourceFile, found);
  return mergeDeclarations(found);
}

function visitList(
  nodes: readonly ts.Node[],
  containers: readonly string[],
  sourceFile: ts.SourceFile,
  found: FoundSymbol[],
): void {
  for (const node of nodes) visitNode(node, containers, sourceFile, found);
}

function visitNode(
  node: ts.Node,
  containers: readonly string[],
  sourceFile: ts.SourceFile,
  found: FoundSymbol[],
): void {
  if (ts.isClassDeclaration(node)) {
    const nameNode = node.name;
    if (nameNode !== undefined) {
      emit(found, sourceFile, nameNode, containers, nameNode.text, "class", false, null);
      visitMembers(node.members, [...containers, nameNode.text], sourceFile, found);
    }
    return;
  }
  if (ts.isInterfaceDeclaration(node)) {
    emit(found, sourceFile, node.name, containers, node.name.text, "interface", false, null);
    visitMembers(node.members, [...containers, node.name.text], sourceFile, found);
    return;
  }
  if (ts.isEnumDeclaration(node)) {
    emit(found, sourceFile, node.name, containers, node.name.text, "enum", false, null);
    return;
  }
  if (ts.isTypeAliasDeclaration(node)) {
    emit(found, sourceFile, node.name, containers, node.name.text, "type-alias", false, null);
    return;
  }
  if (ts.isFunctionDeclaration(node)) {
    const nameNode = node.name;
    if (nameNode !== undefined) {
      emit(
        found,
        sourceFile,
        nameNode,
        containers,
        nameNode.text,
        "function",
        false,
        signatureText(node, sourceFile),
      );
    }
    if (node.body !== undefined) {
      visitList(
        node.body.statements,
        nameNode === undefined ? containers : [...containers, nameNode.text],
        sourceFile,
        found,
      );
    }
    return;
  }
  if (ts.isModuleDeclaration(node)) {
    visitModule(node, containers, sourceFile, found);
    return;
  }
  if (ts.isBlock(node)) {
    visitList(node.statements, containers, sourceFile, found);
    return;
  }
  ts.forEachChild(node, (child) => visitNode(child, containers, sourceFile, found));
}

function visitModule(
  node: ts.ModuleDeclaration,
  containers: readonly string[],
  sourceFile: ts.SourceFile,
  found: FoundSymbol[],
): void {
  const body = node.body;
  if (body === undefined) return;
  const augmentation = (node.flags & ts.NodeFlags.GlobalAugmentation) !== 0;
  const name = !augmentation && ts.isIdentifier(node.name) ? node.name.text : undefined;
  const next = name === undefined ? containers : [...containers, name];
  if (ts.isModuleBlock(body)) {
    visitList(body.statements, next, sourceFile, found);
    return;
  }
  if (ts.isModuleDeclaration(body)) visitModule(body, next, sourceFile, found);
}

function visitMembers(
  members: readonly ts.Node[],
  containers: readonly string[],
  sourceFile: ts.SourceFile,
  found: FoundSymbol[],
): void {
  for (const member of members) visitMember(member, containers, sourceFile, found);
}

function visitMember(
  member: ts.Node,
  containers: readonly string[],
  sourceFile: ts.SourceFile,
  found: FoundSymbol[],
): void {
  if (ts.isConstructorDeclaration(member)) {
    for (const parameter of member.parameters) {
      if (!ts.isParameterPropertyDeclaration(parameter, member)) continue;
      const name = propertyName(parameter.name);
      if (name === undefined) continue;
      emit(found, sourceFile, parameter.name, containers, name, "property", false, null);
    }
    if (member.body !== undefined) visitList(member.body.statements, containers, sourceFile, found);
    return;
  }
  if (ts.isMethodDeclaration(member) || ts.isMethodSignature(member)) {
    const name = propertyName(member.name);
    if (name === undefined) return;
    const isStaticMember = hasModifier(member, ts.SyntaxKind.StaticKeyword);
    emit(
      found,
      sourceFile,
      member.name,
      containers,
      name,
      "method",
      isStaticMember,
      signatureText(member, sourceFile),
    );
    if (ts.isMethodDeclaration(member) && member.body !== undefined) {
      visitList(
        member.body.statements,
        nestedContainers(containers, name, isStaticMember),
        sourceFile,
        found,
      );
    }
    return;
  }
  if (ts.isGetAccessor(member) || ts.isSetAccessor(member)) {
    const name = propertyName(member.name);
    if (name === undefined) return;
    const isStaticMember = hasModifier(member, ts.SyntaxKind.StaticKeyword);
    emit(found, sourceFile, member.name, containers, name, "property", isStaticMember, null);
    if (member.body !== undefined) {
      visitList(
        member.body.statements,
        nestedContainers(containers, name, isStaticMember),
        sourceFile,
        found,
      );
    }
    return;
  }
  if (ts.isPropertyDeclaration(member) || ts.isPropertySignature(member)) {
    const name = propertyName(member.name);
    if (name === undefined) return;
    const isStaticMember = hasModifier(member, ts.SyntaxKind.StaticKeyword);
    emit(found, sourceFile, member.name, containers, name, "property", isStaticMember, null);
    if (ts.isPropertyDeclaration(member) && member.initializer !== undefined) {
      visitNode(
        member.initializer,
        nestedContainers(containers, name, isStaticMember),
        sourceFile,
        found,
      );
    }
    return;
  }
  if (ts.isClassStaticBlockDeclaration(member)) {
    visitList(member.body.statements, containers, sourceFile, found);
  }
}

function emit(
  found: FoundSymbol[],
  sourceFile: ts.SourceFile,
  nameNode: ts.Node,
  containers: readonly string[],
  name: string,
  kind: SourceSymbolKind,
  isStaticMember: boolean,
  signature: string | null,
): void {
  const position = sourceFile.getLineAndCharacterOfPosition(nameNode.getStart(sourceFile, false));
  found.push({
    qualifiedName: nestedContainers(containers, name, isStaticMember).join("."),
    kind,
    line: position.line + 1,
    column: position.character + 1,
    signature,
  });
}

function nestedContainers(
  containers: readonly string[],
  name: string,
  isStaticMember: boolean,
): string[] {
  const parts = [...containers];
  if (isStaticMember) parts.push("<static>");
  parts.push(name);
  return parts;
}

function propertyName(name: ts.PropertyName): string | undefined {
  if (ts.isIdentifier(name) || ts.isPrivateIdentifier(name)) return name.text;
  return undefined;
}

function hasModifier(node: ts.Node, kind: ts.SyntaxKind): boolean {
  return (
    ts.canHaveModifiers(node) &&
    (ts.getModifiers(node)?.some((modifier) => modifier.kind === kind) ?? false)
  );
}

function signatureText(
  node: ts.FunctionDeclaration | ts.MethodDeclaration | ts.MethodSignature,
  sourceFile: ts.SourceFile,
): string {
  const end = node.type === undefined ? parameterListEnd(node, sourceFile) : node.type.getEnd();
  return collapseWhitespace(sourceFile, signatureStart(node, sourceFile), end);
}

function parameterListEnd(
  node: ts.FunctionDeclaration | ts.MethodDeclaration | ts.MethodSignature,
  sourceFile: ts.SourceFile,
): number {
  const closeParen = node
    .getChildren(sourceFile)
    .findLast((child) => child.kind === ts.SyntaxKind.CloseParenToken);
  return closeParen === undefined ? node.parameters.end : closeParen.getEnd();
}

function signatureStart(
  node: ts.FunctionDeclaration | ts.MethodDeclaration | ts.MethodSignature,
  sourceFile: ts.SourceFile,
): number {
  const modifiers = ts.canHaveModifiers(node) ? ts.getModifiers(node) : undefined;
  if (modifiers !== undefined) {
    for (const modifier of modifiers) {
      if (!skippedModifiers.has(modifier.kind)) return modifier.getStart(sourceFile, false);
    }
  }
  if (ts.isFunctionDeclaration(node)) return functionKeywordStart(node, sourceFile);
  if (ts.isMethodDeclaration(node) && node.asteriskToken !== undefined) {
    return node.asteriskToken.getStart(sourceFile, false);
  }
  return node.name.getStart(sourceFile, false);
}

function functionKeywordStart(node: ts.FunctionDeclaration, sourceFile: ts.SourceFile): number {
  const scanner = ts.createScanner(
    sourceFile.languageVersion,
    false,
    sourceFile.languageVariant,
    sourceFile.text,
    undefined,
    node.getStart(sourceFile, false),
  );
  while (scanner.getTextPos() < node.end) {
    const kind = scanner.scan();
    if (kind === ts.SyntaxKind.EndOfFileToken) break;
    if (kind === ts.SyntaxKind.FunctionKeyword) return scanner.getTokenStart();
  }
  return node.name?.getStart(sourceFile, false) ?? node.getStart(sourceFile, false);
}

function collapseWhitespace(sourceFile: ts.SourceFile, start: number, end: number): string {
  const spans = commentSpans(sourceFile, start, end);
  let text = "";
  let cursor = start;
  for (const span of spans) {
    if (span.pos > cursor) text += sourceFile.text.slice(cursor, span.pos);
    cursor = Math.max(cursor, span.end);
  }
  if (cursor < end) text += sourceFile.text.slice(cursor, end);
  return text.replace(/\s+/g, " ").trim();
}

function commentSpans(
  sourceFile: ts.SourceFile,
  start: number,
  end: number,
): Array<{ pos: number; end: number }> {
  const spans: Array<{ pos: number; end: number }> = [];
  const scanner = ts.createScanner(
    sourceFile.languageVersion,
    false,
    sourceFile.languageVariant,
    sourceFile.text,
    undefined,
    start,
  );
  while (scanner.getTextPos() < end) {
    const kind = scanner.scan();
    if (kind === ts.SyntaxKind.EndOfFileToken) break;
    if (scanner.getTokenStart() >= end) break;
    if (
      kind !== ts.SyntaxKind.SingleLineCommentTrivia &&
      kind !== ts.SyntaxKind.MultiLineCommentTrivia
    ) {
      continue;
    }
    spans.push({
      pos: Math.max(scanner.getTokenStart(), start),
      end: Math.min(scanner.getTokenEnd(), end),
    });
  }
  return spans;
}

function mergeDeclarations(found: readonly FoundSymbol[]): IndexedDeclaration[] {
  const order: IndexedDeclaration[] = [];
  const byName = new Map<string, IndexedDeclaration>();
  for (const item of found) {
    const existing = byName.get(item.qualifiedName);
    if (existing === undefined) {
      const created: IndexedDeclaration = {
        qualifiedName: item.qualifiedName,
        kind: item.kind,
        line: item.line,
        column: item.column,
        signatures: item.signature === null ? [] : [item.signature],
      };
      byName.set(item.qualifiedName, created);
      order.push(created);
      continue;
    }
    if (item.signature !== null) existing.signatures.push(item.signature);
    if (kindRank[item.kind] < kindRank[existing.kind]) {
      existing.kind = item.kind;
      existing.line = item.line;
      existing.column = item.column;
    }
  }
  return order;
}
