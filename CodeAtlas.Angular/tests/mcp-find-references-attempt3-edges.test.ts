import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import Database from "better-sqlite3";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { workspaceStorage } from "../src/workspace-storage.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

/**
 * Attempt 3 edges for `find_references`.
 *
 * MCP-CONTRACT.md: a use is an identifier. The declaration name is not a use.
 * `reference` is every use that is not a call, `new`, or tagged template.
 * An object-literal shorthand reads the in-scope value. A destructuring shorthand
 * reads the property. A rest element is not a property use. A parenthesized
 * assignment target is not a destructuring target the checker accepts, so it is
 * a value read of the in-scope symbol.
 */

type ReferenceHit = {
  targetId: string;
  kind: string;
  project: string;
  path: string;
  line: number;
  column: number;
};

type SymbolHit = {
  id: string;
  kind: string;
  qualifiedName: string;
  project: string;
  path: string;
  line: number;
  column: number;
  signatures: string[];
};

type FindEnvelope = {
  contractVersion?: number;
  command?: string;
  success?: boolean;
  responseToolVersion?: string;
  warningCount?: number;
  freshness: { checkedAtUtc: string } | null;
  coverage?: unknown;
  result?: { references?: ReferenceHit[]; symbols?: SymbolHit[] };
  error?: { code: string; message: string; diagnostics: string[] };
};

type Spot = {
  kind: "invocation" | "reference";
  file: string;
  needle: string;
  name: string;
};

type NamedHit = {
  hit: ReferenceHit;
  name: string;
};

const projectName = "shop";
const cartTotal = "shop::src/cart.ts#Cart.total";
const cartPrice = "shop::src/cart.ts#Cart.price";
const totalFunction = "shop::src/cart.ts#total";
const decoratedClass = "shop::src/decorator.ts#Decorated";
const authGuard = "shop::src/auth.ts#authGuard";
const authInterceptor = "shop::src/auth.ts#authInterceptor";
const panelQuantity = "shop::src/panel.ts#Panel.quantity";
const panelQuantityChange = "shop::src/panel.ts#Panel.quantityChange";

const cartSource = `export class Cart {
  total = 1;
  price = 2;
}

export class Wrap {
  a = new Cart();
}

export function total(): number {
  return 1;
}
`;

const literalsSource = `import { total } from "./cart.js";

function take(value: unknown): void {
  void value;
}

export function returned(): unknown {
  return { total };
}

export function argument(): unknown {
  take({ total });
  return 0;
}

export function element(): unknown {
  return [{ total }];
}

export function nested(): unknown {
  return { box: { total } };
}

export class Holder {
  made = { total };
}

export function besideMethod(): unknown {
  return { total, run() { return 1; } };
}
`;

const copiesSource = `import { Cart } from "./cart.js";

function take(value: unknown): void {
  void value;
}

export function copied(cart: Cart): number {
  const total = cart.total;
  const made = { total };
  take({ total });
  const row = [{ total }];
  const box = { inner: { total } };
  const back = { total };
  const withRun = { total, run() { return 1; } };
  void made;
  void row;
  void box;
  void back;
  void withRun;
  return total;
}

const fieldTotal = new Cart().total;

export class FieldCopy {
  made = { fieldTotal };
}
`;

const assignSource = `import { Cart, Wrap } from "./cart.js";

export function plain(cart: Cart): void {
  let total = 0;
  ({ total } = cart);
  void total;
}

export function nested(wrap: Wrap): void {
  let total = 0;
  ({ a: { total } } = wrap);
  void total;
}

export function fromArray(carts: Cart[]): void {
  let total = 0;
  [{ total }] = carts;
  void total;
}

export function forOfConst(carts: Cart[]): number {
  let sum = 0;
  for (const { total } of carts) {
    sum += total;
  }
  return sum;
}

export function forOfAssign(carts: Cart[]): void {
  let total = 0;
  for ({ total } of carts) {
    void total;
  }
}
`;

const parenSource = `import { Cart, Wrap, total } from "./cart.js";

export function parenthesized(cart: Cart): void {
  ({ total }) = cart;
}

export function parenthesizedFor(carts: Cart[]): void {
  for (({ total }) of carts) {
    return;
  }
}

export function nestedParenthesized(wrap: Wrap): void {
  ({ a: ({ total }) } = wrap);
}

export function arrayParenthesized(carts: Cart[]): void {
  [({ total })] = carts;
}

export function deepParenthesized(cart: Cart): void {
  ((({ total }))) = cart;
}

export function deepFor(carts: Cart[]): void {
  for ((({ total })) of carts) {
    return;
  }
}
`;

const shadowSource = `import { Cart, Wrap } from "./cart.js";

export function witness(cart: Cart): number {
  return cart.total;
}

export function parenthesized(cart: Cart): void {
  let total = 0;
  ({ total }) = cart;
  void total;
}

export function parenthesizedFor(carts: Cart[]): void {
  let total = 0;
  for (({ total }) of carts) {
    void total;
  }
}

export function nestedParenthesized(wrap: Wrap): void {
  let total = 0;
  ({ a: ({ total }) } = wrap);
  void total;
}

export function arrayParenthesized(carts: Cart[]): void {
  let total = 0;
  [({ total })] = carts;
  void total;
}

export function deepParenthesized(cart: Cart): void {
  let total = 0;
  ((({ total }))) = cart;
  void total;
}

export function deepFor(carts: Cart[]): void {
  let total = 0;
  for ((({ total })) of carts) {
    void total;
  }
}
`;

const restSource = `import { Cart } from "./cart.js";

export const restParam = ({ total, ...price }: Cart) => 0;

export function restAssign(cart: Cart): void {
  let total = 0;
  let price: object = {};
  ({ total, ...price } = cart);
  void total;
  void price;
}
`;

const decoratorSource = `const selector = "app-box";

@Component({ selector })
export class Decorated {}

export function make(): Decorated {
  return new Decorated();
}
`;

const authSource = `export const authGuard: CanActivateFn = () => true;

export const authInterceptor: HttpInterceptorFn = () => null;
`;

const routesSource = `import { authGuard, authInterceptor } from "./auth.js";

export const routes = [{ path: "cart", canActivate: [authGuard] }];

export function clients(): void {
  provideHttpClient(withInterceptors([authInterceptor]));
}
`;

const panelSource = `export class Panel {
  quantity = model(0);
}

export function read(panel: Panel): number {
  return panel.quantity;
}

export function write(panel: Panel): void {
  panel.quantity = 1;
}
`;

const workspaceFiles = files({
  "src/cart.ts": cartSource,
  "src/literals.ts": literalsSource,
  "src/copies.ts": copiesSource,
  "src/assign.ts": assignSource,
  "src/paren.ts": parenSource,
  "src/shadow.ts": shadowSource,
  "src/rest.ts": restSource,
  "src/decorator.ts": decoratorSource,
  "src/auth.ts": authSource,
  "src/routes.ts": routesSource,
  "src/panel.ts": panelSource,
});

function files(code: Record<string, string>): Record<string, string> {
  return {
    "package.json": `${JSON.stringify(
      { name: "shop", private: true, dependencies: { "@angular/core": "19.2.0" } },
      null,
      2,
    )}\n`,
    "angular.json": `${JSON.stringify(
      {
        version: 1,
        projects: {
          shop: {
            projectType: "application",
            root: "",
            sourceRoot: "src",
            architect: {
              build: {
                builder: "@angular-devkit/build-angular:application",
                options: { tsConfig: "tsconfig.app.json" },
              },
            },
          },
        },
      },
      null,
      2,
    )}\n`,
    "tsconfig.app.json": `${JSON.stringify(
      {
        compilerOptions: {
          strict: true,
          target: "ES2022",
          module: "ES2022",
          moduleResolution: "bundler",
          skipLibCheck: true,
        },
        include: ["src/**/*.ts"],
      },
      null,
      2,
    )}\n`,
    ...code,
  };
}

function compareCodeUnit(left: string, right: string): number {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

function byContract(left: ReferenceHit, right: ReferenceHit): number {
  const pathOrder = compareCodeUnit(left.path, right.path);
  if (pathOrder !== 0) return pathOrder;
  if (left.line !== right.line) return left.line - right.line;
  if (left.column !== right.column) return left.column - right.column;
  return compareCodeUnit(left.kind, right.kind);
}

function locate(targetId: string, spot: Spot): NamedHit {
  const source = workspaceFiles[spot.file];
  if (source === undefined) throw new Error(`missing ${spot.file}`);
  const index = source.indexOf(spot.needle);
  if (index < 0) throw new Error(`missing ${spot.needle} in ${spot.file}`);
  const nameIndex = spot.needle.lastIndexOf(spot.name);
  if (nameIndex < 0) throw new Error(`missing ${spot.name} in ${spot.needle}`);
  const absolute = index + nameIndex;
  const line = source.slice(0, absolute).split("\n").length;
  const lineStart = source.lastIndexOf("\n", absolute - 1) + 1;
  return {
    name: spot.name,
    hit: {
      targetId,
      kind: spot.kind,
      project: projectName,
      path: spot.file,
      line,
      column: absolute - lineStart + 1,
    },
  };
}

function namedHits(targetId: string, spots: readonly Spot[]): NamedHit[] {
  return spots
    .map((spot) => locate(targetId, spot))
    .sort((left, right) => {
      return byContract(left.hit, right.hit);
    });
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

function parseToolText(result: unknown): FindEnvelope {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("tool did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("tool did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("tool did not return a text block");
  return JSON.parse(block.text) as FindEnvelope;
}

function expectNamed(
  references: readonly ReferenceHit[] | undefined,
  named: readonly NamedHit[],
  source: string,
): void {
  const actual = references ?? [];
  expect(actual).toEqual(named.map((entry) => entry.hit));
  expect(actual.map((hit, index) => textAt(source, hit, named[index]?.name ?? ""))).toEqual(
    named.map((entry) => entry.name),
  );
}

function textAt(source: string, hit: ReferenceHit, name: string): string {
  const lineText = source.split("\n")[hit.line - 1] ?? "";
  return lineText.slice(hit.column - 1, hit.column - 1 + name.length);
}

function inFile(references: readonly ReferenceHit[], file: string): ReferenceHit[] {
  return references.filter((hit) => hit.path === file);
}

async function createWorkspace(
  contents: Record<string, string>,
): Promise<{ path: string; remove: () => Promise<void> }> {
  const root = await mkdtemp(path.join(tmpdir(), "codeatlas-ng-attempt3-"));
  for (const [rel, text] of Object.entries(contents)) {
    const destination = path.join(root, ...rel.split("/"));
    await mkdir(path.dirname(destination), { recursive: true });
    await writeFile(destination, text);
  }
  return { path: root, remove: () => rm(root, { recursive: true, force: true }) };
}

async function indexWorkspace(workspacePath: string): Promise<void> {
  const indexed = await runCli(["index", "--workspace", workspacePath]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
}

function errorEnvelope(command: string, code: string, message: string) {
  return {
    contractVersion: 1,
    command,
    success: false,
    responseToolVersion: "0.00.01",
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: {
      code,
      message,
      diagnostics: [] as string[],
    },
  };
}

/** Task 05 schema. This branch stores references and those kinds at schema 4. */
function rewriteAsSchemaVersion3(workspacePath: string): void {
  const storage = workspaceStorage(workspacePath);
  const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as {
    manifestVersion: number;
    generationId: string;
    schemaVersion: number;
    workspace: string;
    artifacts: { "index.db": { length: number; sha256: string } };
  };
  const databasePath = path.join(
    storage.directory,
    "generations",
    manifest.generationId,
    "index.db",
  );
  const database = new Database(databasePath);
  try {
    database.prepare("UPDATE metadata SET value = ? WHERE key = ?").run("3", "schemaVersion");
  } finally {
    database.close();
  }
  const bytes = readFileSync(databasePath);
  manifest.schemaVersion = 3;
  manifest.artifacts["index.db"] = {
    length: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
  writeFileSync(storage.currentJson, `${JSON.stringify(manifest, null, 2)}\n`);
}

describe("MCP find_references attempt 3 edges", () => {
  let workspacePath = "";
  let removeWorkspace: (() => Promise<void>) | undefined;
  let connection: Awaited<ReturnType<typeof connectInProcess>> | undefined;

  beforeAll(async () => {
    const workspace = await createWorkspace(workspaceFiles);
    workspacePath = workspace.path;
    removeWorkspace = workspace.remove;
    await indexWorkspace(workspacePath);
    connection = await connectInProcess();
  }, 60_000);

  afterAll(async () => {
    await connection?.close();
    await removeWorkspace?.();
  });

  function client(): Awaited<ReturnType<typeof connectInProcess>>["client"] {
    if (connection === undefined) throw new Error("missing connection");
    return connection.client;
  }

  async function findReferences(id: string): Promise<ReferenceHit[]> {
    const result = await client().callTool({
      name: "find_references",
      arguments: { workspace: workspacePath, id },
    });
    expect(result.isError).toBeFalsy();
    const envelope = parseToolText(result);
    expect(envelope.success).toBe(true);
    return envelope.result?.references ?? [];
  }

  it("records object-literal shorthand of an indexed function and not the declaration", async () => {
    // Arrange. Return, argument, array element, nested literal, class field, and a method beside it.
    const named = namedHits(totalFunction, [
      { kind: "reference", file: "src/literals.ts", needle: "import { total }", name: "total" },
      { kind: "reference", file: "src/literals.ts", needle: "return { total }", name: "total" },
      { kind: "reference", file: "src/literals.ts", needle: "take({ total })", name: "total" },
      { kind: "reference", file: "src/literals.ts", needle: "return [{ total }]", name: "total" },
      {
        kind: "reference",
        file: "src/literals.ts",
        needle: "return { box: { total } }",
        name: "total",
      },
      { kind: "reference", file: "src/literals.ts", needle: "made = { total }", name: "total" },
      {
        kind: "reference",
        file: "src/literals.ts",
        needle: "return { total, run() { return 1; } }",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(totalFunction);

    // Assert
    expectNamed(inFile(references, "src/literals.ts"), named, literalsSource);
    expect(inFile(references, "src/cart.ts")).toEqual([]);
    expect(references.every((hit) => hit.kind === "reference")).toBe(true);
  });

  it("records Cart.total only at the property read when object-literal shorthand copies a local", async () => {
    // Arrange. `{ total }` copies the local. The class field copies `fieldTotal`, not `Cart.total`.
    const named = namedHits(cartTotal, [
      {
        kind: "reference",
        file: "src/copies.ts",
        needle: "const total = cart.total",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/copies.ts",
        needle: "new Cart().total",
        name: "total",
      },
    ]);
    const shorthands = [
      "const made = { total }",
      "take({ total })",
      "const row = [{ total }]",
      "const box = { inner: { total } }",
      "const back = { total }",
      "const withRun = { total, run() { return 1; } }",
      "made = { fieldTotal }",
    ].map((needle) =>
      locate(cartTotal, {
        kind: "reference",
        file: "src/copies.ts",
        needle,
        name: needle.includes("fieldTotal") ? "fieldTotal" : "total",
      }),
    );

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expectNamed(inFile(references, "src/copies.ts"), named, copiesSource);
    for (const shorthand of shorthands) {
      expect(
        references.some(
          (hit) => hit.line === shorthand.hit.line && hit.column === shorthand.hit.column,
        ),
      ).toBe(false);
    }
  });

  it("records destructuring assignment, nested patterns, array assignment, and for-of heads", async () => {
    // Arrange. Locals introduced by the bindings are not further references.
    const named = namedHits(cartTotal, [
      { kind: "reference", file: "src/assign.ts", needle: "({ total } = cart)", name: "total" },
      {
        kind: "reference",
        file: "src/assign.ts",
        needle: "({ a: { total } } = wrap)",
        name: "total",
      },
      { kind: "reference", file: "src/assign.ts", needle: "[{ total }] = carts", name: "total" },
      {
        kind: "reference",
        file: "src/assign.ts",
        needle: "for (const { total } of carts)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/assign.ts",
        needle: "for ({ total } of carts)",
        name: "total",
      },
    ]);
    const locals = ["void total;", "sum += total", "let total = 0"].map((needle) =>
      locate(cartTotal, { kind: "reference", file: "src/assign.ts", needle, name: "total" }),
    );

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expectNamed(inFile(references, "src/assign.ts"), named, assignSource);
    for (const local of locals) {
      expect(
        inFile(references, "src/assign.ts").some(
          (hit) => hit.line === local.hit.line && hit.column === local.hit.column,
        ),
      ).toBe(false);
    }
  });

  it("records ({ total }) = cart and for (({ total }) of carts) as value reads of the in-scope function", async () => {
    // Arrange. The parentheses wrap the literal, so the property API is not used.
    const named = namedHits(totalFunction, [
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "import { Cart, Wrap, total }",
        name: "total",
      },
      { kind: "reference", file: "src/paren.ts", needle: "({ total }) = cart", name: "total" },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "for (({ total }) of carts)",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(totalFunction);

    // Assert
    const recorded = inFile(references, "src/paren.ts");
    for (const expected of named) {
      expect(
        recorded.filter(
          (hit) => hit.line === expected.hit.line && hit.column === expected.hit.column,
        ),
      ).toEqual([expected.hit]);
    }
    expect(recorded.every((hit) => hit.kind === "reference")).toBe(true);
  });

  it("records only the parenthesized value reads of the indexed function", async () => {
    // Arrange. The declaration in cart.ts is not a use.
    const named = namedHits(totalFunction, [
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "import { Cart, Wrap, total }",
        name: "total",
      },
      { kind: "reference", file: "src/paren.ts", needle: "({ total }) = cart", name: "total" },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "for (({ total }) of carts)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "({ a: ({ total }) } = wrap)",
        name: "total",
      },
      { kind: "reference", file: "src/paren.ts", needle: "[({ total })] = carts", name: "total" },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "((({ total }))) = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "for ((({ total })) of carts)",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(totalFunction);

    // Assert
    expectNamed(inFile(references, "src/paren.ts"), named, parenSource);
    expect(inFile(references, "src/cart.ts")).toEqual([]);
  });

  it("records nested parenthesized targets as value reads of the in-scope function", async () => {
    // Arrange. A parenthesis anywhere above the literal keeps the value symbol.
    const named = namedHits(totalFunction, [
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "({ a: ({ total }) } = wrap)",
        name: "total",
      },
      { kind: "reference", file: "src/paren.ts", needle: "[({ total })] = carts", name: "total" },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "((({ total }))) = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/paren.ts",
        needle: "for ((({ total })) of carts)",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(totalFunction);

    // Assert
    const recorded = inFile(references, "src/paren.ts");
    for (const expected of named) {
      expect(
        recorded.filter(
          (hit) => hit.line === expected.hit.line && hit.column === expected.hit.column,
        ),
      ).toEqual([expected.hit]);
    }
  });

  it("does not record a parenthesized target as Cart.total when the in-scope value is the function", async () => {
    // Arrange
    const spots = [
      "({ total }) = cart",
      "for (({ total }) of carts)",
      "({ a: ({ total }) } = wrap)",
      "[({ total })] = carts",
      "((({ total }))) = cart",
      "for ((({ total })) of carts)",
    ].map((needle) =>
      locate(cartTotal, { kind: "reference", file: "src/paren.ts", needle, name: "total" }),
    );

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expect(inFile(references, "src/paren.ts")).toEqual([]);
    for (const spot of spots) {
      expect(
        references.some((hit) => hit.line === spot.hit.line && hit.column === spot.hit.column),
      ).toBe(false);
    }
  });

  it("does not record a parenthesized target as Cart.total when the in-scope value is a local", async () => {
    // Arrange. The witness proves the file was indexed. The local `total` is not an indexed symbol.
    const named = namedHits(cartTotal, [
      { kind: "reference", file: "src/shadow.ts", needle: "return cart.total", name: "total" },
    ]);
    const spots = [
      "({ total }) = cart",
      "for (({ total }) of carts)",
      "({ a: ({ total }) } = wrap)",
      "[({ total })] = carts",
      "((({ total }))) = cart",
      "for ((({ total })) of carts)",
    ].map((needle) =>
      locate(cartTotal, { kind: "reference", file: "src/shadow.ts", needle, name: "total" }),
    );

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expectNamed(inFile(references, "src/shadow.ts"), named, shadowSource);
    for (const spot of spots) {
      expect(
        references.some((hit) => hit.line === spot.hit.line && hit.column === spot.hit.column),
      ).toBe(false);
    }
  });

  it("records Cart.total beside a rest parameter and a rest assignment", async () => {
    // Arrange
    const named = namedHits(cartTotal, [
      {
        kind: "reference",
        file: "src/rest.ts",
        needle: "({ total, ...price }: Cart)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/rest.ts",
        needle: "({ total, ...price } = cart)",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expectNamed(inFile(references, "src/rest.ts"), named, restSource);
  });

  it("does not record Cart.price at a rest parameter or a rest assignment", async () => {
    // Arrange
    const spots = ["({ total, ...price }: Cart)", "({ total, ...price } = cart)"].map((needle) =>
      locate(cartPrice, { kind: "reference", file: "src/rest.ts", needle, name: "price" }),
    );

    // Act
    const references = await findReferences(cartPrice);

    // Assert
    expect(references).toEqual([]);
    for (const spot of spots) {
      expect(
        references.some((hit) => hit.line === spot.hit.line && hit.column === spot.hit.column),
      ).toBe(false);
    }
  });

  it("indexes a shorthand in a decorator argument", async () => {
    // Arrange. `selector` is a const, so it is not an indexed symbol. The class still is.
    const named = namedHits(decoratedClass, [
      {
        kind: "reference",
        file: "src/decorator.ts",
        needle: "make(): Decorated",
        name: "Decorated",
      },
      {
        kind: "invocation",
        file: "src/decorator.ts",
        needle: "new Decorated()",
        name: "Decorated",
      },
    ]);

    // Act
    const references = await findReferences(decoratedClass);

    // Assert
    expectNamed(references, named, decoratorSource);
  });

  it("returns a const guard's uses in an import and a route canActivate array", async () => {
    // Arrange. The declaration name is not a use. The array element is not a call.
    const named = namedHits(authGuard, [
      {
        kind: "reference",
        file: "src/routes.ts",
        needle: "import { authGuard, authInterceptor }",
        name: "authGuard",
      },
      {
        kind: "reference",
        file: "src/routes.ts",
        needle: "canActivate: [authGuard]",
        name: "authGuard",
      },
    ]);

    // Act
    const references = await findReferences(authGuard);

    // Assert
    expectNamed(references, named, routesSource);
    expect(inFile(references, "src/auth.ts")).toEqual([]);
  });

  it("returns an interceptor's uses in an import and provideHttpClient(withInterceptors)", async () => {
    // Arrange
    const named = namedHits(authInterceptor, [
      {
        kind: "reference",
        file: "src/routes.ts",
        needle: "import { authGuard, authInterceptor }",
        name: "authInterceptor",
      },
      {
        kind: "reference",
        file: "src/routes.ts",
        needle: "withInterceptors([authInterceptor])",
        name: "authInterceptor",
      },
    ]);

    // Act
    const references = await findReferences(authInterceptor);

    // Assert
    expectNamed(references, named, routesSource);
    expect(inFile(references, "src/auth.ts")).toEqual([]);
  });

  it("returns uses of a model property at the property name", async () => {
    // Arrange
    const named = namedHits(panelQuantity, [
      {
        kind: "reference",
        file: "src/panel.ts",
        needle: "return panel.quantity",
        name: "quantity",
      },
      {
        kind: "reference",
        file: "src/panel.ts",
        needle: "panel.quantity = 1",
        name: "quantity",
      },
    ]);

    // Act
    const references = await findReferences(panelQuantity);

    // Assert
    expectNamed(references, named, panelSource);
  });

  it("does not give a model property's uses to the Change output", async () => {
    // Arrange. The property's reads stay on Panel.quantity.

    // Act
    const references = await findReferences(panelQuantityChange);

    // Assert
    expect(references).toEqual([]);
  });
});

describe("MCP find_references attempt 3 schema 3", () => {
  async function schema3Workspace(): Promise<{ path: string; remove: () => Promise<void> }> {
    const workspace = await createWorkspace(
      files({
        "src/cart.ts": cartSource,
      }),
    );
    try {
      await indexWorkspace(workspace.path);
    } catch (error) {
      await workspace.remove();
      throw error;
    }
    rewriteAsSchemaVersion3(workspace.path);
    return workspace;
  }

  it("refuses a schema version 3 index from find_references with unsupported_index", async () => {
    // Arrange
    const workspace = await schema3Workspace();
    const connection = await connectInProcess();
    const message = `Unsupported index schema. Run codeatlas-ng index --workspace ${workspace.path} to rebuild it.`;

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_references", "unsupported_index", message),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses a schema version 3 index from find_symbol with unsupported_index", async () => {
    // Arrange
    const workspace = await schema3Workspace();
    const connection = await connectInProcess();
    const message = `Unsupported index schema. Run codeatlas-ng index --workspace ${workspace.path} to rebuild it.`;

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("find_symbol", "unsupported_index", message),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
