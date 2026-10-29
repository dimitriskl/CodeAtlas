import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import Database from "better-sqlite3";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { workspaceStorage } from "../src/workspace-storage.js";
import { copyReferencesWorkspace } from "./support/fixture.js";
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

type ReferenceHit = {
  targetId: string;
  kind: string;
  project: string;
  path: string;
  line: number;
  column: number;
};

type FindEnvelope = {
  contractVersion?: number;
  command?: string;
  success?: boolean;
  responseToolVersion?: string;
  warningCount?: number;
  freshness?: { checkedAtUtc?: string; diagnostics?: string[] } | null;
  coverage?: unknown;
  pagination?: {
    offset: number;
    limit: number;
    returned: number;
    total: number;
    hasMore: boolean;
    nextOffset: number | null;
    snapshotId: string | null;
    continuationSupported: boolean;
  };
  result?: { references: ReferenceHit[] };
  error?: { code: string; message: string; diagnostics: string[] };
};

type Spot = {
  kind: "invocation" | "reference";
  file: string;
  needle: string;
  name: string;
};

const project = "shop";
const cartTotal = "shop::src/cart.ts#Cart.total";
const bagTotal = "shop::src/cart.ts#Bag.total";
const cartClass = "shop::src/cart.ts#Cart";
const formatId = "shop::src/cart.ts#Cart.<static>.format";
const hiddenId = "shop::src/cart.ts#Cart.#hidden";
const doubleId = "shop::src/cart.ts#Cart.double";
const emptyUnused = "shop::src/cart.ts#Empty.unused";
const actionId = "shop::src/cart.ts#Holder.action";
const countId = "shop::src/cart.ts#Holder.count";
const pingId = "shop::src/cart.ts#ping";
const readId = "shop::src/cart.ts#read";
const htmlId = "shop::src/cart.ts#html";
const defaultCart = "shop::src/cart.ts#DefaultCart";
const priceId = "shop::src/cart.ts#DefaultCart.price";
const labelId = "shop::src/cart.ts#Label";
const specialId = "shop::src/cart.ts#Special";
const extraId = "shop::src/cart.ts#Special.extra";
const taggedId = "shop::src/cart.ts#Tagged";
const identityId = "shop::src/uses.ts#identity";
const sharedA = "shop::src/ping-a.ts#shared";
const sharedB = "shop::src/ping-b.ts#shared";
const boxA = "shop::src/box-a.ts#SharedBox";
const boxB = "shop::src/box-b.ts#SharedBox";

const usesLines = [
  'import { Bag, Cart, Holder, Special, Tagged, html, ping, read } from "./cart.js";',
  'import { type Label } from "./cart.js";',
  'import DefaultCart from "./cart.js";',
  'import * as kit from "./cart.js";',
  "",
  "export function calls(cart: Cart, bag: Bag, holder: Holder): number {",
  "  const first = cart.total();",
  "  const optional = cart?.total();",
  "  const optionalCall = cart.total?.();",
  "  const paren = (cart.total)();",
  "  const asserted = cart.total!();",
  "  const cast = (cart.total as () => number)();",
  "  const value = cart.total;",
  "  const method = cart.total;",
  "  const indirect = method();",
  "  const made = new Cart();",
  "  const madeParen = new (Cart)();",
  "  const madeNs = new kit.Cart();",
  "  const formatted = Cart.format(cart);",
  "  const formattedNs = kit.Cart.format(cart);",
  "  const staticValue = Cart.format;",
  "  const pinged = ping();",
  "  const pingValue = ping;",
  "  const pingNs = kit.ping();",
  '  const readValue = read("a");',
  "  const tagged = html`x`;",
  '  const key = "total";',
  '  const dynamic = cart["total"]() + cart[key]();',
  "  const special = new Special();",
  "  const labelled: Label = new Tagged();",
  "  const box: Array<Cart> = [made];",
  "  const taken = identity<Cart>(made);",
  "  const held = holder.action();",
  "  const heldValue = holder.action;",
  "  const count = holder.count;",
  "  const calledCount = holder.count();",
  "  const chained = cart?.total?.();",
  "  const { total: renamed } = cart;",
  "  const billed = bag.total();",
  "  const sat = made satisfies Cart;",
  "  const angled = <Cart>made;",
  "  const madeDefault = new DefaultCart();",
  "  const priced = madeDefault.price();",
  "  void indirect;",
  "  void optional;",
  "  void optionalCall;",
  "  void paren;",
  "  void asserted;",
  "  void cast;",
  "  void value;",
  "  void method;",
  "  void madeParen;",
  "  void madeNs;",
  "  void formatted;",
  "  void formattedNs;",
  "  void staticValue;",
  "  void pinged;",
  "  void pingValue;",
  "  void pingNs;",
  "  void readValue;",
  "  void tagged;",
  "  void dynamic;",
  "  void special;",
  "  void labelled;",
  "  void box;",
  "  void taken;",
  "  void held;",
  "  void heldValue;",
  "  void count;",
  "  void calledCount;",
  "  void chained;",
  "  void renamed;",
  "  void billed;",
  "  void sat;",
  "  void angled;",
  "  void priced;",
  "  return first;",
  "}",
  "",
  "function identity<T extends Cart>(value: T): T {",
  "  return value;",
  "}",
  "",
  "class Sub extends Special implements Label {",
  '  text = "sub";',
  "  more(): number {",
  "    return super.extra();",
  "  }",
  "}",
  "",
  "type CartType = typeof Cart;",
  "",
];

const sources: Record<string, string> = {
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
              options: { tsConfig: "tsconfig.app.json", styles: ["src/styles.css"] },
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
  "src/styles.css": ".cart { color: black; }\n",
  "src/app.html": '<p>{{ total() }}</p>\n<app-cart [total]="total"></app-cart>\n',
  "src/view.ts": 'export const view = {\n  templateUrl: "./app.html",\n};\n',
  "src/cart.ts": `export class Cart {
  total(): number {
    return 1;
  }

  static format(value: Cart): string {
    return String(value);
  }

  double(): number {
    return this.total() * 2;
  }

  #hidden(): number {
    return this.#hidden();
  }

  reveal(): number {
    return 0;
  }
}

export class Bag {
  total(): number {
    return 2;
  }
}

export class Special extends Cart {
  extra(): number {
    return super.total();
  }
}

export interface Label {
  text: string;
}

export class Tagged implements Label {
  text = "ok";
}

export function html(text: TemplateStringsArray): string {
  return String(text[0] ?? "");
}

export function ping(): number {
  return 1;
}

export function read(value: string): string;
export function read(value: number): number;
export function read(value: string | number): string | number {
  return value;
}

export class Empty {
  unused(): number {
    return 0;
  }
}

export class Holder {
  count = 1;
  action: () => number = () => 1;
}

export default class DefaultCart {
  price(): number {
    return 3;
  }
}
`,
  "src/uses.ts": usesLines.join("\n"),
  "src/reexport.ts": `export { Cart as Tote, Bag } from "./cart.js";
export { default as DefaultCart } from "./cart.js";
`,
  "src/star.ts": `export * from "./cart.js";
export * from "./uses.js";
`,
  "src/A-calls.ts": `import { ping } from "./cart.js";
export const upper = ping();
`,
  "src/z-calls.ts": `import { ping } from "./cart.js";
export const zed = ping();
`,
  "src/é-calls.ts": `import { ping } from "./cart.js";
export const accent = ping();
`,
  "src/ping-a.ts": `export {};
declare global {
  function shared(value: string): string;
}
`,
  "src/ping-b.ts": `export {};
declare global {
  function shared(value: number): number;
}
`,
  "src/ping-use.ts": `export {};
export const sharedValue = shared("a");
`,
  "src/box-a.ts": `export {};
declare global {
  interface SharedBox {
    a: number;
  }
}
`,
  "src/box-b.ts": `export {};
declare global {
  interface SharedBox {
    b: string;
  }
}
`,
  "src/box-use.ts": `export {};
export const box: SharedBox = { a: 1, b: "b" };
`,
};

const cartTotalSpots: Spot[] = [
  { kind: "invocation", file: "src/cart.ts", needle: "this.total()", name: "total" },
  { kind: "invocation", file: "src/cart.ts", needle: "super.total()", name: "total" },
  { kind: "invocation", file: "src/uses.ts", needle: "const first = cart.total()", name: "total" },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const optional = cart?.total()",
    name: "total",
  },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const optionalCall = cart.total?.()",
    name: "total",
  },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const paren = (cart.total)()",
    name: "total",
  },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const asserted = cart.total!()",
    name: "total",
  },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const cast = (cart.total as () => number)()",
    name: "total",
  },
  { kind: "reference", file: "src/uses.ts", needle: "const value = cart.total;", name: "total" },
  { kind: "reference", file: "src/uses.ts", needle: "const method = cart.total;", name: "total" },
  { kind: "reference", file: "src/uses.ts", needle: "const { total: renamed }", name: "total" },
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const chained = cart?.total?.()",
    name: "total",
  },
];

const cartClassSpots: Spot[] = [
  { kind: "reference", file: "src/cart.ts", needle: "value: Cart", name: "Cart" },
  { kind: "reference", file: "src/cart.ts", needle: "extends Cart", name: "Cart" },
  {
    kind: "reference",
    file: "src/reexport.ts",
    needle: "export { Cart as Tote, Bag }",
    name: "Cart",
  },
  {
    kind: "reference",
    file: "src/reexport.ts",
    needle: "export { Cart as Tote, Bag }",
    name: "Tote",
  },
  { kind: "reference", file: "src/uses.ts", needle: "Bag, Cart, Holder", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "cart: Cart", name: "Cart" },
  { kind: "invocation", file: "src/uses.ts", needle: "const made = new Cart()", name: "Cart" },
  { kind: "invocation", file: "src/uses.ts", needle: "new (Cart)()", name: "Cart" },
  { kind: "invocation", file: "src/uses.ts", needle: "new kit.Cart()", name: "Cart" },
  {
    kind: "reference",
    file: "src/uses.ts",
    needle: "const formatted = Cart.format(cart)",
    name: "Cart",
  },
  { kind: "reference", file: "src/uses.ts", needle: "kit.Cart.format(cart)", name: "Cart" },
  {
    kind: "reference",
    file: "src/uses.ts",
    needle: "const staticValue = Cart.format;",
    name: "Cart",
  },
  { kind: "reference", file: "src/uses.ts", needle: "Array<Cart>", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "identity<Cart>", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "satisfies Cart", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "<Cart>made", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "extends Cart", name: "Cart" },
  { kind: "reference", file: "src/uses.ts", needle: "typeof Cart", name: "Cart" },
];

const formatSpots: Spot[] = [
  {
    kind: "invocation",
    file: "src/uses.ts",
    needle: "const formatted = Cart.format(cart)",
    name: "format",
  },
  { kind: "invocation", file: "src/uses.ts", needle: "kit.Cart.format(cart)", name: "format" },
  {
    kind: "reference",
    file: "src/uses.ts",
    needle: "const staticValue = Cart.format;",
    name: "format",
  },
];

const pingSpots: Spot[] = [
  { kind: "reference", file: "src/A-calls.ts", needle: "import { ping }", name: "ping" },
  { kind: "invocation", file: "src/A-calls.ts", needle: "upper = ping()", name: "ping" },
  { kind: "reference", file: "src/uses.ts", needle: "html, ping, read", name: "ping" },
  { kind: "invocation", file: "src/uses.ts", needle: "const pinged = ping()", name: "ping" },
  { kind: "reference", file: "src/uses.ts", needle: "const pingValue = ping;", name: "ping" },
  { kind: "invocation", file: "src/uses.ts", needle: "kit.ping()", name: "ping" },
  { kind: "reference", file: "src/z-calls.ts", needle: "import { ping }", name: "ping" },
  { kind: "invocation", file: "src/z-calls.ts", needle: "zed = ping()", name: "ping" },
  { kind: "reference", file: "src/é-calls.ts", needle: "import { ping }", name: "ping" },
  { kind: "invocation", file: "src/é-calls.ts", needle: "accent = ping()", name: "ping" },
];

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

function hit(targetId: string, spot: Spot): ReferenceHit {
  const source = sources[spot.file];
  if (source === undefined) throw new Error(`missing ${spot.file}`);
  const index = source.indexOf(spot.needle);
  if (index < 0) throw new Error(`missing ${spot.needle} in ${spot.file}`);
  // A needle can contain the name as a prefix of an earlier word (`formatted` before `format`).
  const nameIndex = spot.needle.lastIndexOf(spot.name);
  if (nameIndex < 0) throw new Error(`missing ${spot.name} in ${spot.needle}`);
  const absolute = index + nameIndex;
  const line = source.slice(0, absolute).split("\n").length;
  const lineStart = source.lastIndexOf("\n", absolute - 1) + 1;
  return {
    targetId,
    kind: spot.kind,
    project,
    path: spot.file,
    line,
    column: absolute - lineStart + 1,
  };
}

function hits(targetId: string, spots: readonly Spot[]): ReferenceHit[] {
  return spots.map((spot) => hit(targetId, spot)).sort(byContract);
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
    throw new Error("find_references did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("find_references did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("find_references did not return a text block");
  return JSON.parse(block.text) as FindEnvelope;
}

function errorEnvelope(code: string, message: string) {
  return {
    contractVersion: 1,
    command: "find_references",
    success: false,
    responseToolVersion: "0.00.01",
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: { code, message, diagnostics: [] as string[] },
  };
}

async function createUsesWorkspace(): Promise<{ path: string; remove: () => Promise<void> }> {
  const root = await mkdtemp(path.join(tmpdir(), "codeatlas-ng-uses-"));
  for (const [rel, text] of Object.entries(sources)) {
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

function indexDigest(workspacePath: string): string {
  const storage = workspaceStorage(workspacePath);
  const manifest = JSON.parse(readFileSync(storage.currentJson, "utf8")) as {
    generationId: string;
  };
  const databasePath = path.join(
    storage.directory,
    "generations",
    manifest.generationId,
    "index.db",
  );
  const current = readFileSync(storage.currentJson);
  const database = readFileSync(databasePath);
  return createHash("sha256").update(current).update(database).digest("hex");
}

function rewriteGeneration(workspacePath: string, schema: number, dropReferences: boolean): void {
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
    database
      .prepare("UPDATE metadata SET value = ? WHERE key = ?")
      .run(String(schema), "schemaVersion");
    if (dropReferences) database.exec('DROP TABLE IF EXISTS "references"');
  } finally {
    database.close();
  }
  const bytes = readFileSync(databasePath);
  manifest.schemaVersion = schema;
  manifest.artifacts["index.db"] = {
    length: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
  writeFileSync(storage.currentJson, `${JSON.stringify(manifest, null, 2)}\n`);
}

describe("MCP find_references edges", () => {
  let workspacePath = "";
  let removeWorkspace: (() => Promise<void>) | undefined;
  let connection: Awaited<ReturnType<typeof connectInProcess>> | undefined;
  let digest = "";

  beforeAll(async () => {
    const workspace = await createUsesWorkspace();
    workspacePath = workspace.path;
    removeWorkspace = workspace.remove;
    await indexWorkspace(workspacePath);
    digest = indexDigest(workspacePath);
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

  async function findReferences(args: Record<string, unknown>): Promise<{
    isError: boolean;
    envelope: FindEnvelope;
  }> {
    const result = await client().callTool({
      name: "find_references",
      arguments: { workspace: workspacePath, ...args },
    });
    return { isError: result.isError === true, envelope: parseToolText(result) };
  }

  function expectUses(envelope: FindEnvelope, expected: ReferenceHit[], limit = 100): void {
    expect(envelope.success).toBe(true);
    expect(envelope.command).toBe("find_references");
    expect(envelope.result?.references).toEqual(expected);
    expect(envelope.pagination).toMatchObject({
      offset: 0,
      limit,
      returned: expected.length,
      total: expected.length,
      hasMore: false,
      nextOffset: null,
      continuationSupported: true,
    });
    expect(envelope.pagination?.snapshotId).toMatch(/^[0-9a-f]{32}$/);
    expect(envelope.result?.references?.every((row) => row.path.endsWith(".ts"))).toBe(true);
  }

  it("classifies method calls, property reads, optional calls, and skips element access", async () => {
    // Arrange
    const expected = hits(cartTotal, cartTotalSpots);

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
    expect(envelope.result?.references?.[0] && Object.keys(envelope.result.references[0])).toEqual([
      "targetId",
      "kind",
      "project",
      "path",
      "line",
      "column",
    ]);
  });

  it("classifies imports, new, heritage, generics, type queries, and re-exports of a class", async () => {
    // Arrange
    const expected = hits(cartClass, cartClassSpots);

    // Act
    const { isError, envelope } = await findReferences({ id: cartClass });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("classifies a static call as invocation and an uncalled static name as reference", async () => {
    // Arrange
    const expected = hits(formatId, formatSpots);

    // Act
    const { isError, envelope } = await findReferences({ id: formatId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a private call and not the private declaration", async () => {
    // Arrange
    const expected = hits(hiddenId, [
      { kind: "invocation", file: "src/cart.ts", needle: "this.#hidden()", name: "#hidden" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: hiddenId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a called property as invocation and an uncalled property as reference", async () => {
    // Arrange
    const expected = hits(actionId, [
      { kind: "invocation", file: "src/uses.ts", needle: "holder.action()", name: "action" },
      { kind: "reference", file: "src/uses.ts", needle: "holder.action;", name: "action" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: actionId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a call of a non-callable property name as invocation", async () => {
    // Arrange
    const expected = hits(countId, [
      { kind: "reference", file: "src/uses.ts", needle: "holder.count;", name: "count" },
      { kind: "invocation", file: "src/uses.ts", needle: "holder.count()", name: "count" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: countId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("returns an empty page for a symbol that has no uses", async () => {
    // Arrange
    const expected = hits(emptyUnused, []);

    // Act
    const { isError, envelope } = await findReferences({ id: emptyUnused });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
    expect(envelope.warningCount).toBe(0);
    expect(envelope.freshness).toMatchObject({ state: "fresh", isStale: false, changedFiles: [] });
  });

  it("returns an empty page for a method that is never called", async () => {
    // Arrange
    const expected = hits(doubleId, []);

    // Act
    const { isError, envelope } = await findReferences({ id: doubleId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("returns one invocation for an overload set", async () => {
    // Arrange
    const expected = hits(readId, [
      { kind: "reference", file: "src/uses.ts", needle: "ping, read", name: "read" },
      { kind: "invocation", file: "src/uses.ts", needle: 'read("a")', name: "read" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: readId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a tagged template as invocation", async () => {
    // Arrange
    const expected = hits(htmlId, [
      { kind: "reference", file: "src/uses.ts", needle: "Tagged, html, ping", name: "html" },
      { kind: "invocation", file: "src/uses.ts", needle: "const tagged = html`", name: "html" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: htmlId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a default import, a default re-export, and new as invocation", async () => {
    // Arrange
    const expected = hits(defaultCart, [
      {
        kind: "reference",
        file: "src/reexport.ts",
        needle: "export { default as DefaultCart }",
        name: "default",
      },
      {
        kind: "reference",
        file: "src/reexport.ts",
        needle: "export { default as DefaultCart }",
        name: "DefaultCart",
      },
      {
        kind: "reference",
        file: "src/uses.ts",
        needle: "import DefaultCart from",
        name: "DefaultCart",
      },
      { kind: "invocation", file: "src/uses.ts", needle: "new DefaultCart()", name: "DefaultCart" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: defaultCart });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records an instance method call as invocation", async () => {
    // Arrange
    const expected = hits(priceId, [
      { kind: "invocation", file: "src/uses.ts", needle: "madeDefault.price()", name: "price" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: priceId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records heritage and a type-only import as reference", async () => {
    // Arrange
    const expected = hits(labelId, [
      { kind: "reference", file: "src/cart.ts", needle: "implements Label", name: "Label" },
      { kind: "reference", file: "src/uses.ts", needle: "import { type Label }", name: "Label" },
      { kind: "reference", file: "src/uses.ts", needle: "labelled: Label", name: "Label" },
      { kind: "reference", file: "src/uses.ts", needle: "implements Label", name: "Label" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: labelId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records new as invocation and extends as reference", async () => {
    // Arrange
    const expected = hits(specialId, [
      {
        kind: "reference",
        file: "src/uses.ts",
        needle: "Holder, Special, Tagged",
        name: "Special",
      },
      { kind: "invocation", file: "src/uses.ts", needle: "new Special()", name: "Special" },
      { kind: "reference", file: "src/uses.ts", needle: "extends Special", name: "Special" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: specialId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records super.extra() as an invocation of the method", async () => {
    // Arrange
    const expected = hits(extraId, [
      { kind: "invocation", file: "src/uses.ts", needle: "super.extra()", name: "extra" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: extraId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("does not return a look-alike call from another class", async () => {
    // Arrange
    const expected = hits(bagTotal, [
      { kind: "invocation", file: "src/uses.ts", needle: "bag.total()", name: "total" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: bagTotal });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a constructor call of an imported class as invocation", async () => {
    // Arrange
    const expected = hits(taggedId, [
      { kind: "reference", file: "src/uses.ts", needle: "Special, Tagged, html", name: "Tagged" },
      { kind: "invocation", file: "src/uses.ts", needle: "new Tagged()", name: "Tagged" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: taggedId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("records a generic call as invocation of the function and reference of the type argument", async () => {
    // Arrange
    const expected = hits(identityId, [
      { kind: "invocation", file: "src/uses.ts", needle: "identity<Cart>", name: "identity" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: identityId });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("orders references by path, line, and column in UTF-16 code unit order", async () => {
    // Arrange
    const expected = hits(pingId, pingSpots);

    // Act
    const { isError, envelope } = await findReferences({ id: pingId, limit: 1000 });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected, 1000);
    expect(expected.map((row) => row.path)).toEqual([
      "src/A-calls.ts",
      "src/A-calls.ts",
      "src/uses.ts",
      "src/uses.ts",
      "src/uses.ts",
      "src/uses.ts",
      "src/z-calls.ts",
      "src/z-calls.ts",
      "src/é-calls.ts",
      "src/é-calls.ts",
    ]);
  });

  it("keeps that order when the rows are read one per page", async () => {
    // Arrange
    const expected = hits(pingId, pingSpots);
    const collected: ReferenceHit[] = [];
    let offset = 0;
    let snapshotId: string | undefined;

    // Act
    for (let page = 0; page < expected.length + 1; page += 1) {
      const args: Record<string, unknown> = { id: pingId, offset, limit: 1 };
      if (snapshotId !== undefined) args.snapshotId = snapshotId;
      const { isError, envelope } = await findReferences(args);
      expect(isError).toBe(false);
      const rows = envelope.result?.references ?? [];
      collected.push(...rows);
      expect(envelope.pagination?.limit).toBe(1);
      expect(envelope.pagination?.total).toBe(expected.length);
      expect(envelope.pagination?.snapshotId).toMatch(/^[0-9a-f]{32}$/);
      if (snapshotId !== undefined) expect(envelope.pagination?.snapshotId).toBe(snapshotId);
      snapshotId = envelope.pagination?.snapshotId ?? undefined;
      if (envelope.pagination?.hasMore !== true) {
        expect(envelope.pagination?.nextOffset).toBeNull();
        expect(rows).toHaveLength(1);
        break;
      }
      const nextOffset = envelope.pagination.nextOffset;
      expect(nextOffset).toBe(offset + 1);
      if (nextOffset === null) throw new Error("missing nextOffset");
      offset = nextOffset;
    }

    // Assert
    expect(collected).toEqual(expected);
  });

  it("returns the same call for each merged global function", async () => {
    // Arrange
    const call = hits(sharedA, [
      { kind: "invocation", file: "src/ping-use.ts", needle: 'shared("a")', name: "shared" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: sharedB });

    // Assert
    expect(isError).toBe(false);
    expectUses(
      envelope,
      call.map((row) => ({ ...row, targetId: sharedB })),
    );
    const other = await findReferences({ id: sharedA });
    expect(other.isError).toBe(false);
    expectUses(other.envelope, call);
  });

  it("returns the same type use for each merged global interface", async () => {
    // Arrange
    const typeUse = hits(boxA, [
      { kind: "reference", file: "src/box-use.ts", needle: "box: SharedBox", name: "SharedBox" },
    ]);

    // Act
    const { isError, envelope } = await findReferences({ id: boxA });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, typeUse);
    const other = await findReferences({ id: boxB });
    expect(other.isError).toBe(false);
    expectUses(
      other.envelope,
      typeUse.map((row) => ({ ...row, targetId: boxB })),
    );
  });

  it("returns only invocations when kinds requests invocation and a kind that is not stored", async () => {
    // Arrange
    const expected = hits(
      cartTotal,
      cartTotalSpots.filter((spot) => spot.kind === "invocation"),
    );

    // Act
    const { isError, envelope } = await findReferences({
      id: cartTotal,
      kinds: ["invocation", "template-usage"],
    });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("returns an empty page when every requested kind is accepted but not stored", async () => {
    // Arrange
    const expected = hits(cartTotal, []);

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, kinds: ["template-call"] });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("returns the same rows when kinds lists invocation twice", async () => {
    // Arrange
    const expected = hits(
      cartTotal,
      cartTotalSpots.filter((spot) => spot.kind === "invocation"),
    );

    // Act
    const { isError, envelope } = await findReferences({
      id: cartTotal,
      kinds: ["invocation", "invocation"],
    });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected);
  });

  it("returns the symbol project rows and an empty page for another project", async () => {
    // Arrange
    const expected = hits(cartTotal, cartTotalSpots);

    // Act
    const matched = await findReferences({ id: cartTotal, project: "shop" });

    // Assert
    expect(matched.isError).toBe(false);
    expectUses(matched.envelope, expected);
    const other = await findReferences({ id: cartTotal, project: "catalog" });
    expect(other.isError).toBe(false);
    expectUses(other.envelope, []);
  });

  it("accepts the maximum limit", async () => {
    // Arrange
    const expected = hits(pingId, pingSpots);

    // Act
    const { isError, envelope } = await findReferences({ id: pingId, limit: 1000 });

    // Assert
    expect(isError).toBe(false);
    expectUses(envelope, expected, 1000);
  });

  it("returns no rows when offset is past the last match", async () => {
    // Arrange
    const expected = hits(cartTotal, cartTotalSpots);
    const first = await findReferences({ id: cartTotal, limit: 1 });
    const snapshotId = first.envelope.pagination?.snapshotId;

    // Act
    const { isError, envelope } = await findReferences({
      id: cartTotal,
      offset: expected.length,
      limit: 1,
      snapshotId,
    });

    // Assert
    expect(isError).toBe(false);
    expect(envelope.result?.references).toEqual([]);
    expect(envelope.pagination).toMatchObject({
      offset: expected.length,
      limit: 1,
      returned: 0,
      total: expected.length,
      hasMore: false,
      nextOffset: null,
      continuationSupported: true,
      snapshotId,
    });
  });

  it("continues with a snapshotId taken from find_symbol", async () => {
    // Arrange
    const expected = hits(cartTotal, cartTotalSpots);
    const symbol = await client().callTool({
      name: "find_symbol",
      arguments: { workspace: workspacePath, name: "total", limit: 1 },
    });
    const symbolEnvelope = parseToolText(symbol);
    const snapshotId = symbolEnvelope.pagination?.snapshotId;
    const second = expected[1];
    if (second === undefined) throw new Error("missing second reference");

    // Act
    const { isError, envelope } = await findReferences({
      id: cartTotal,
      offset: 1,
      limit: 1,
      snapshotId,
    });

    // Assert
    expect(isError).toBe(false);
    expect(envelope.result?.references).toEqual([second]);
    expect(envelope.pagination?.snapshotId).toBe(snapshotId);
  });

  it("continues with a snapshotId taken from a different symbol query", async () => {
    // Arrange
    const expected = hits(cartTotal, cartTotalSpots);
    const other = await findReferences({ id: emptyUnused, limit: 1 });
    const snapshotId = other.envelope.pagination?.snapshotId;

    // Act
    const { isError, envelope } = await findReferences({
      id: cartTotal,
      offset: 1,
      limit: 1,
      snapshotId,
    });

    // Assert
    expect(isError).toBe(false);
    expect(envelope.result?.references).toEqual(expected.slice(1, 2));
    expect(envelope.pagination?.snapshotId).toBe(snapshotId);
  });

  it("returns snapshot_mismatch for a snapshot id that is not the generation", async () => {
    // Arrange
    const snapshotId = "not-the-generation";

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, snapshotId });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope(
        "snapshot_mismatch",
        "The index was replaced. Start a new query at offset zero.",
      ),
    );
  });

  it("does not write the index while it reads references", async () => {
    // Arrange
    const before = digest;

    // Act
    const { isError } = await findReferences({ id: cartTotal });

    // Assert
    expect(isError).toBe(false);
    expect(indexDigest(workspacePath)).toBe(before);
  });

  it("returns symbol_not_found when the id is not trimmed", async () => {
    // Arrange
    const id = ` ${cartTotal}`;

    // Act
    const { isError, envelope } = await findReferences({ id });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("symbol_not_found", "Symbol id was not found."));
  });

  it("returns symbol_not_found for an id that only shares a prefix", async () => {
    // Arrange
    const id = "shop::src/cart.ts#Cart.tot";

    // Act
    const { isError, envelope } = await findReferences({ id });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("symbol_not_found", "Symbol id was not found."));
  });

  it("rejects an empty id", async () => {
    // Arrange
    const id = "";

    // Act
    const { isError, envelope } = await findReferences({ id });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("invalid_arguments", "id must not be empty."));
  });

  it("rejects a missing id with the tool validation error", async () => {
    // Arrange
    const argumentsWithoutId = { workspace: workspacePath };

    // Act
    const result = await client().callTool({
      name: "find_references",
      arguments: argumentsWithoutId,
    });

    // Assert
    expect(result.isError).toBe(true);
    const content = result.content;
    if (!Array.isArray(content)) throw new Error("find_references did not return content");
    const block: unknown = content[0];
    if (!isTextBlock(block)) throw new Error("find_references did not return a text block");
    expect(block.text).toBe(
      "MCP error -32602: Input validation error: Invalid arguments for tool find_references: Required at id",
    );
  });

  it("rejects an empty kinds array", async () => {
    // Arrange
    const kinds: string[] = [];

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, kinds });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("invalid_arguments", "kinds must not be empty."));
  });

  it("rejects an unknown reference kind", async () => {
    // Arrange
    const kinds = ["nope"];

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, kinds });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("invalid_arguments", "Unknown reference kind."));
  });

  it("rejects an empty project", async () => {
    // Arrange
    const projectName = "";

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, project: projectName });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("invalid_arguments", "project must not be empty."));
  });

  it("rejects an empty snapshotId", async () => {
    // Arrange
    const snapshotId = "";

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, snapshotId });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(errorEnvelope("invalid_arguments", "snapshotId must not be empty."));
  });

  it("rejects an offset greater than zero without snapshotId", async () => {
    // Arrange
    const offset = 1;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, offset });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "offset greater than zero requires snapshotId."),
    );
  });

  it("rejects a negative offset", async () => {
    // Arrange
    const offset = -1;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, offset });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "offset must be a nonnegative integer."),
    );
  });

  it("rejects a non-integer offset", async () => {
    // Arrange
    const offset = 1.5;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, offset });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "offset must be a nonnegative integer."),
    );
  });

  it("rejects limit 0", async () => {
    // Arrange
    const limit = 0;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, limit });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "limit must be an integer from 1 through 1000."),
    );
  });

  it("rejects a negative limit", async () => {
    // Arrange
    const limit = -1;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, limit });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "limit must be an integer from 1 through 1000."),
    );
  });

  it("rejects a limit above 1000", async () => {
    // Arrange
    const limit = 1001;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, limit });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "limit must be an integer from 1 through 1000."),
    );
  });

  it("rejects a non-integer limit", async () => {
    // Arrange
    const limit = 1.5;

    // Act
    const { isError, envelope } = await findReferences({ id: cartTotal, limit });

    // Assert
    expect(isError).toBe(true);
    expect(envelope).toEqual(
      errorEnvelope("invalid_arguments", "limit must be an integer from 1 through 1000."),
    );
  });
});

describe("MCP find_references freshness and schema", () => {
  it("refuses a stale index and lists the changed files in code-unit order", async () => {
    // Arrange
    const workspace = await createUsesWorkspace();
    await indexWorkspace(workspace.path);
    await writeFile(
      path.join(workspace.path, "src", "z-calls.ts"),
      'import { ping } from "./cart.js";\nexport const zed = ping() + 1;\n',
    );
    await writeFile(
      path.join(workspace.path, "src", "é-calls.ts"),
      'import { ping } from "./cart.js";\nexport const accent = ping() + 1;\n',
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: pingId },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = parseToolText(result);
      expect(envelope.pagination).toBeUndefined();
      expect(envelope.result).toBeUndefined();
      const checkedAtUtc = envelope.freshness?.checkedAtUtc;
      expect(checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...envelope.freshness, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "find_references",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: {
          state: "stale",
          checkedAtUtc: "TIMESTAMP",
          isStale: true,
          changedFiles: ["src/z-calls.ts", "src/é-calls.ts"],
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
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and reports warningCount when a stylesheet changes", async () => {
    // Arrange
    const workspace = await createUsesWorkspace();
    await indexWorkspace(workspace.path);
    await writeFile(path.join(workspace.path, "src", "styles.css"), ".cart { color: white; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: emptyUnused },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = parseToolText(result);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness?.diagnostics).toEqual(["Style or asset changed: src/styles.css."]);
      expect(envelope.success).toBe(true);
      expect(envelope.result?.references).toEqual([]);
      expect(envelope.pagination?.total).toBe(0);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns symbol_not_found and writes nothing when no generation is published", async () => {
    // Arrange
    const workspace = await createUsesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("symbol_not_found", "Symbol id was not found."),
      );
      expect(existsSync(path.join(workspace.path, ".codeatlas"))).toBe(false);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns snapshot_mismatch when snapshotId is supplied and no generation is published", async () => {
    // Arrange
    const workspace = await createUsesWorkspace();
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, snapshotId: "abc" },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope(
          "snapshot_mismatch",
          "The index was replaced. Start a new query at offset zero.",
        ),
      );
      expect(existsSync(path.join(workspace.path, ".codeatlas"))).toBe(false);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns snapshot_mismatch for a snapshotId from another workspace", async () => {
    // Arrange
    const workspace = await createUsesWorkspace();
    const other = await createUsesWorkspace();
    await indexWorkspace(workspace.path);
    await indexWorkspace(other.path);
    const connection = await connectInProcess();

    try {
      const otherPage = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: other.path, id: cartTotal, limit: 1 },
      });
      const snapshotId = parseToolText(otherPage).pagination?.snapshotId;

      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal, snapshotId },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope(
          "snapshot_mismatch",
          "The index was replaced. Start a new query at offset zero.",
        ),
      );
    } finally {
      await connection.close();
      await workspace.remove();
      await other.remove();
    }
  });

  it("refuses a schema version 2 index from find_symbol with unsupported_index", async () => {
    // Arrange
    const workspace = await copyReferencesWorkspace();
    await indexWorkspace(workspace.path);
    rewriteGeneration(workspace.path, 2, true);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart" },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = parseToolText(result);
      expect(envelope).toEqual({
        contractVersion: 1,
        command: "find_symbol",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: null,
        coverage: null,
        error: {
          code: "unsupported_index",
          message: `Unsupported index schema. Run codeatlas-ng index --workspace ${workspace.path} to rebuild it.`,
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns invalid_index when a schema 3 generation has no references table", async () => {
    // Arrange
    const workspace = await copyReferencesWorkspace();
    await indexWorkspace(workspace.path);
    rewriteGeneration(workspace.path, 3, true);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_references",
        arguments: { workspace: workspace.path, id: cartTotal },
      });

      // Assert
      expect(result.isError).toBe(true);
      expect(parseToolText(result)).toEqual(
        errorEnvelope("invalid_index", "Snapshot is missing the references table."),
      );
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});

describe("codeatlas-ng find_references command", () => {
  it("is not a CLI command", async () => {
    // Arrange
    const args = ["find_references", "--workspace", "."] as const;

    // Act
    const result = await runCli(args);

    // Assert
    expect(result.exitCode).toBe(2);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual({
      contractVersion: 1,
      command: "find_references",
      success: false,
      responseToolVersion: "0.00.01",
      warningCount: 0,
      freshness: null,
      coverage: null,
      error: {
        code: "invalid_arguments",
        message: "Unknown command.",
        diagnostics: [],
      },
    });
  });
});
