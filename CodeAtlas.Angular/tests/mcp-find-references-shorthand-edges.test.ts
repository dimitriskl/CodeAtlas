import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";

/**
 * Shorthand edges for `find_references`.
 *
 * MCP-CONTRACT.md records every use of an indexed symbol. A use's line and column
 * point at the identifier. The symbol's own declaration is not a use. A value that
 * is not called is `reference`. Destructuring shorthand of an indexed property is
 * that kind of read. An object-literal shorthand is property creation: it reads the
 * in-scope value, and it does not read a member of some other object.
 */

type ReferenceHit = {
  targetId: string;
  kind: string;
  project: string;
  path: string;
  line: number;
  column: number;
};

type FindEnvelope = {
  success?: boolean;
  result?: { references?: ReferenceHit[]; symbols?: unknown[] };
  pagination?: { total?: number };
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

const project = "shop";
const cartTotal = "shop::src/cart.ts#Cart.total";
const cartPrice = "shop::src/cart.ts#Cart.price";
const cartItems = "shop::src/cart.ts#Cart.items";
const cartCounts = "shop::src/cart.ts#Cart.counts";
const lineLength = "shop::src/cart.ts#Line.length";
const totalFunction = "shop::src/cart.ts#total";

const cartSource = `export class Line {
  length = 0;
}

export class Cart {
  total = 1;
  price = 2;
  items = new Line();
  counts: number[] = [];
}
`;

const edgesSource = `import { Cart } from "./cart.js";

export function defaults(cart: Cart): number {
  const { total = 0 } = cart;
  return total;
}

export function nested(cart: Cart): number {
  const { items: { length } } = cart;
  return length;
}

export function nestedCounts(cart: Cart): number {
  const { counts: { length } } = cart;
  return length;
}

export function mixed(cart: Cart): number {
  const { total, price: renamed } = cart;
  return total + renamed;
}

export const read = ({ total }: Cart): number => total;

export class Reader {
  constructor({ total }: Cart) {
    void total;
  }

  take({ total }: Cart): number {
    return total;
  }
}

export function restOf(cart: Cart): number {
  const { total, ...rest } = cart;
  void rest;
  return total;
}

export function restNamedPrice(cart: Cart): number {
  const { total, ...price } = cart;
  void price;
  return total;
}

export function fromArray(cart: Cart): Cart {
  const [total] = [cart];
  return total;
}

export function assign(cart: Cart): void {
  let total: number;
  ({ total } = cart);
  void total;
}

export function rows(carts: Cart[]): number {
  const [{ total }] = carts;
  return total;
}
`;

const literalCartSource = `export class Cart {
  total = 1;
}

export function total(): number {
  return 1;
}
`;

const literalSource = `import { Cart, total } from "./cart.js";

export function literal(cart: Cart): number {
  const total = cart.total;
  const made = { total };
  void made;
  return total;
}

export function box(): unknown {
  return { total };
}
`;

const destructuringFiles = workspaceFiles({
  "src/cart.ts": cartSource,
  "src/edges.ts": edgesSource,
});

const literalFiles = workspaceFiles({
  "src/cart.ts": literalCartSource,
  "src/literal.ts": literalSource,
});

function workspaceFiles(code: Record<string, string>): Record<string, string> {
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

function locate(files: Record<string, string>, targetId: string, spot: Spot): NamedHit {
  const source = files[spot.file];
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
      project,
      path: spot.file,
      line,
      column: absolute - lineStart + 1,
    },
  };
}

function namedHits(
  files: Record<string, string>,
  targetId: string,
  spots: readonly Spot[],
): NamedHit[] {
  return spots
    .map((spot) => locate(files, targetId, spot))
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

function onLine(
  references: readonly ReferenceHit[] | undefined,
  file: string,
  line: number,
): ReferenceHit[] {
  return (references ?? []).filter((hit) => hit.path === file && hit.line === line);
}

async function createWorkspace(
  files: Record<string, string>,
): Promise<{ path: string; remove: () => Promise<void> }> {
  const root = await mkdtemp(path.join(tmpdir(), "codeatlas-ng-shorthand-"));
  for (const [rel, text] of Object.entries(files)) {
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

describe("MCP find_references shorthand edges", () => {
  let workspacePath = "";
  let removeWorkspace: (() => Promise<void>) | undefined;
  let connection: Awaited<ReturnType<typeof connectInProcess>> | undefined;

  beforeAll(async () => {
    const workspace = await createWorkspace(destructuringFiles);
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

  it("records a shorthand property with a default as a reference at the identifier", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total = 0 } = cart",
        name: "total",
      },
    ]);
    const declared = named[0];
    if (declared === undefined) throw new Error("missing default shorthand");
    const local = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "return total;",
      name: "total",
    });

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    const recorded = onLine(references, "src/edges.ts", declared.hit.line);
    expect(recorded).toEqual([declared.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing recorded default");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
    expect(onLine(references, "src/edges.ts", local.hit.line)).toEqual([]);
  });

  it("records nested shorthand of an indexed property at the property name and the inner name", async () => {
    // Arrange
    const items = namedHits(destructuringFiles, cartItems, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { items: { length } } = cart",
        name: "items",
      },
    ]);
    const length = namedHits(destructuringFiles, lineLength, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { items: { length } } = cart",
        name: "length",
      },
    ]);

    // Act
    const itemReferences = await findReferences(cartItems);
    const lengthReferences = await findReferences(lineLength);

    // Assert
    expectNamed(itemReferences, items, edgesSource);
    expectNamed(lengthReferences, length, edgesSource);
  });

  it("does not record a nested shorthand of a library member as an indexed reference", async () => {
    // Arrange. `length` on `number[]` is a library property. It is not an indexed
    // symbol, so it is not a row and it is not a use of `Line.length`.
    const counts = namedHits(destructuringFiles, cartCounts, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { counts: { length } } = cart",
        name: "counts",
      },
    ]);
    const libraryLength = locate(destructuringFiles, lineLength, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "const { counts: { length } } = cart",
      name: "length",
    });

    // Act
    const countReferences = await findReferences(cartCounts);
    const lengthReferences = await findReferences(lineLength);

    // Assert
    expectNamed(countReferences, counts, edgesSource);
    expect(onLine(lengthReferences, "src/edges.ts", libraryLength.hit.line)).toEqual([]);
  });

  it("records shorthand and a renamed binding in one pattern, and not the local name", async () => {
    // Arrange
    const total = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, price: renamed } = cart",
        name: "total",
      },
    ]);
    const price = namedHits(destructuringFiles, cartPrice, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, price: renamed } = cart",
        name: "price",
      },
    ]);
    const renamed = locate(destructuringFiles, cartPrice, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "const { total, price: renamed } = cart",
      name: "renamed",
    });
    const totalHit = total[0];
    const priceHit = price[0];
    if (totalHit === undefined || priceHit === undefined) throw new Error("missing mixed pattern");

    // Act
    const totalReferences = await findReferences(cartTotal);
    const priceReferences = await findReferences(cartPrice);

    // Assert
    const recordedTotal = onLine(totalReferences, "src/edges.ts", totalHit.hit.line);
    const recordedPrice = onLine(priceReferences, "src/edges.ts", priceHit.hit.line);
    expect(recordedTotal).toEqual([totalHit.hit]);
    expect(recordedPrice).toEqual([priceHit.hit]);
    const totalUse = recordedTotal[0];
    const priceUse = recordedPrice[0];
    if (totalUse === undefined || priceUse === undefined) throw new Error("missing mixed uses");
    expect(textAt(edgesSource, totalUse, "total")).toBe("total");
    expect(textAt(edgesSource, priceUse, "price")).toBe("price");
    expect(
      priceReferences.some(
        (hit) => hit.column === renamed.hit.column && hit.line === renamed.hit.line,
      ),
    ).toBe(false);
  });

  it("records a shorthand arrow-function parameter as a reference at the identifier", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "export const read = ({ total }: Cart)",
        name: "total",
      },
    ]);
    const body = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "number => total",
      name: "total",
    });
    const parameter = named[0];
    if (parameter === undefined) throw new Error("missing arrow parameter");

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    const recorded = onLine(references, "src/edges.ts", parameter.hit.line);
    expect(recorded).toEqual([parameter.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing arrow use");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
    expect(
      references.some((hit) => hit.line === body.hit.line && hit.column === body.hit.column),
    ).toBe(false);
  });

  it("records a shorthand constructor parameter as a reference at the identifier", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "constructor({ total }: Cart)",
        name: "total",
      },
    ]);
    const body = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "void total;",
      name: "total",
    });
    const parameter = named[0];
    if (parameter === undefined) throw new Error("missing constructor parameter");

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    const recorded = references.filter(
      (hit) => hit.line === parameter.hit.line && hit.column === parameter.hit.column,
    );
    expect(recorded).toEqual([parameter.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing constructor use");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
    expect(onLine(references, "src/edges.ts", body.hit.line)).toEqual([]);
  });

  it("records a shorthand method parameter as a reference at the identifier", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "take({ total }: Cart)",
        name: "total",
      },
    ]);
    const parameter = named[0];
    if (parameter === undefined) throw new Error("missing method parameter");

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    const recorded = onLine(references, "src/edges.ts", parameter.hit.line);
    expect(recorded).toEqual([parameter.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing method use");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
  });

  it("records shorthand beside a rest element and does not invent a member for rest", async () => {
    // Arrange
    const total = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, ...rest } = cart",
        name: "total",
      },
    ]);
    const rest = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "const { total, ...rest } = cart",
      name: "rest",
    });
    const totalHit = total[0];
    if (totalHit === undefined) throw new Error("missing rest shorthand");

    // Act
    const totalReferences = await findReferences(cartTotal);
    const priceReferences = await findReferences(cartPrice);
    const symbol = await client().callTool({
      name: "find_symbol",
      arguments: { workspace: workspacePath, name: "rest" },
    });

    // Assert
    expect(resultIsError(symbol)).toBe(false);
    const symbolEnvelope = parseToolText(symbol);
    expect(symbolEnvelope.result?.symbols).toEqual([]);
    expect(symbolEnvelope.pagination?.total).toBe(0);
    const recorded = onLine(totalReferences, "src/edges.ts", totalHit.hit.line);
    expect(recorded).toEqual([totalHit.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing rest shorthand");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
    expect(
      [...totalReferences, ...priceReferences].some(
        (hit) => hit.line === rest.hit.line && hit.column === rest.hit.column,
      ),
    ).toBe(false);
  });

  it("does not record a rest element as a reference to the member that shares its name", async () => {
    // Arrange. `...price` binds the leftover object. It does not read `Cart.price`.
    const named = namedHits(destructuringFiles, cartPrice, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, price: renamed } = cart",
        name: "price",
      },
    ]);

    // Act
    const references = await findReferences(cartPrice);

    // Assert
    expectNamed(references, named, edgesSource);
  });

  it("does not record a bare array binding as a member reference", async () => {
    // Arrange
    const element = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const [{ total }] = carts",
        name: "total",
      },
    ]);
    const bare = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "const [total] = [cart]",
      name: "total",
    });
    const shorthand = element[0];
    if (shorthand === undefined) throw new Error("missing element shorthand");

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expect(onLine(references, "src/edges.ts", bare.hit.line)).toEqual([]);
    const recorded = onLine(references, "src/edges.ts", shorthand.hit.line);
    expect(recorded).toEqual([shorthand.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing element shorthand");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
  });

  it("records every shorthand read of Cart.total and skips locals and array bindings", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total = 0 } = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, price: renamed } = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "export const read = ({ total }: Cart)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "constructor({ total }: Cart)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "take({ total }: Cart)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, ...rest } = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const { total, ...price } = cart",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "({ total } = cart)",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "const [{ total }] = carts",
        name: "total",
      },
    ]);

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    expectNamed(references, named, edgesSource);
  });

  it("records a parenthesized shorthand assignment at the identifier", async () => {
    // Arrange
    const named = namedHits(destructuringFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/edges.ts",
        needle: "({ total } = cart)",
        name: "total",
      },
    ]);
    const declared = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "let total: number",
      name: "total",
    });
    const later = locate(destructuringFiles, cartTotal, {
      kind: "reference",
      file: "src/edges.ts",
      needle: "({ total } = cart);\n  void total;",
      name: "total",
    });
    const assigned = named[0];
    if (assigned === undefined) throw new Error("missing assignment");

    // Act
    const references = await findReferences(cartTotal);

    // Assert
    const recorded = onLine(references, "src/edges.ts", assigned.hit.line);
    expect(recorded).toEqual([assigned.hit]);
    const recordedHit = recorded[0];
    if (recordedHit === undefined) throw new Error("missing assignment use");
    expect(textAt(edgesSource, recordedHit, "total")).toBe("total");
    expect(onLine(references, "src/edges.ts", declared.hit.line)).toEqual([]);
    expect(
      references.some((hit) => hit.line === later.hit.line && hit.column === later.hit.column),
    ).toBe(false);
  });
});

describe("MCP find_references object-literal shorthand", () => {
  async function indexedLiteral(): Promise<{
    path: string;
    remove: () => Promise<void>;
    client: Awaited<ReturnType<typeof connectInProcess>>["client"];
    close: () => Promise<void>;
  }> {
    const workspace = await createWorkspace(literalFiles);
    try {
      await indexWorkspace(workspace.path);
    } catch (error) {
      await workspace.remove();
      throw error;
    }
    const connection = await connectInProcess();
    return {
      path: workspace.path,
      remove: workspace.remove,
      client: connection.client,
      close: connection.close,
    };
  }

  async function findReferences(
    client: Awaited<ReturnType<typeof connectInProcess>>["client"],
    workspacePath: string,
    id: string,
  ): Promise<ReferenceHit[]> {
    const result = await client.callTool({
      name: "find_references",
      arguments: { workspace: workspacePath, id },
    });
    expect(result.isError).toBeFalsy();
    const envelope = parseToolText(result);
    expect(envelope.success).toBe(true);
    return envelope.result?.references ?? [];
  }

  it("does not record an object-literal shorthand as a destructuring read of the copied property", async () => {
    // Arrange. `{ total }` copies the local. The read of `Cart.total` is `cart.total` only.
    const named = namedHits(literalFiles, cartTotal, [
      {
        kind: "reference",
        file: "src/literal.ts",
        needle: "const total = cart.total",
        name: "total",
      },
    ]);
    const copied = locate(literalFiles, cartTotal, {
      kind: "reference",
      file: "src/literal.ts",
      needle: "const made = { total }",
      name: "total",
    });
    const session = await indexedLiteral();

    try {
      // Act
      const references = await findReferences(session.client, session.path, cartTotal);

      // Assert
      expectNamed(references, named, literalSource);
      expect(onLine(references, "src/literal.ts", copied.hit.line)).toEqual([]);
    } finally {
      await session.close();
      await session.remove();
    }
  });

  it("records an object-literal shorthand of an indexed function as a value read", async () => {
    // Arrange. The identifier reads `total` and is not a callee, so the kind is `reference`.
    // The function declaration itself is not a use. The local `{ total }` in `literal` is not this function.
    const named = namedHits(literalFiles, totalFunction, [
      {
        kind: "reference",
        file: "src/literal.ts",
        needle: "import { Cart, total }",
        name: "total",
      },
      {
        kind: "reference",
        file: "src/literal.ts",
        needle: "return { total }",
        name: "total",
      },
    ]);
    const localCopy = locate(literalFiles, totalFunction, {
      kind: "reference",
      file: "src/literal.ts",
      needle: "const made = { total }",
      name: "total",
    });
    const session = await indexedLiteral();

    try {
      // Act
      const references = await findReferences(session.client, session.path, totalFunction);

      // Assert
      expectNamed(references, named, literalSource);
      expect(
        references.some(
          (hit) => hit.line === localCopy.hit.line && hit.column === localCopy.hit.column,
        ),
      ).toBe(false);
      expect(references.every((hit) => hit.kind === "reference")).toBe(true);
    } finally {
      await session.close();
      await session.remove();
    }
  });
});

function resultIsError(result: unknown): boolean {
  return (
    typeof result === "object" && result !== null && "isError" in result && result.isError === true
  );
}
