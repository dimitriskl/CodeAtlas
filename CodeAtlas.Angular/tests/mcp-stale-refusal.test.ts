import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
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

const staleMessage = "The saved index is stale.";

function styleOrAssetWarning(filePath: string): string {
  return `Style or asset changed: ${filePath}.`;
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

function toolEnvelope(result: unknown): Record<string, unknown> {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("coverage did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content)) throw new Error("coverage did not return content");
  const block: unknown = content[0];
  if (!isTextBlock(block)) throw new Error("coverage did not return a text block");
  return JSON.parse(block.text) as Record<string, unknown>;
}

function generationCoverage(templateCoverage: "complete" | "partial" | "unknown") {
  return {
    language: "TypeScript",
    symbols: symbolKinds,
    references: "static-source-and-template-references",
    referenceSymbolKinds: referenceKinds,
    templateCoverage,
    runtimeCallGraph: false,
    exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    limitations: templateCoverage === "unknown" ? ["no-index"] : [],
  };
}

function freshness(input: {
  state: "fresh" | "stale";
  changedFiles: string[];
  diagnostics: string[];
}) {
  return {
    state: input.state,
    checkedAtUtc: "TIMESTAMP",
    isStale: input.state === "stale",
    changedFiles: input.changedFiles,
    scope: "tracked-input-content-hashes",
    inputCoverage: {
      status: "complete",
      scope: "tracked-input-content-hashes",
      issueCount: 0,
      reasonCodes: [],
    },
    diagnostics: input.diagnostics,
  };
}

async function indexWorkspace(workspacePath: string): Promise<string> {
  const indexed = await runCli(["index", "--workspace", workspacePath]);
  if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
  const envelope = JSON.parse(indexed.stdout) as { result: { generationId: string } };
  return envelope.result.generationId;
}

function installLeftpad(workspacePath: string): { jsFile: string; tsFile: string } {
  const directory = path.join(workspacePath, "node_modules", "leftpad");
  mkdirSync(directory, { recursive: true });
  const jsFile = path.join(directory, "index.js");
  const tsFile = path.join(directory, "index.ts");
  writeFileSync(jsFile, "module.exports = 1;\n");
  writeFileSync(tsFile, "export const pad = 1;\n");
  writeFileSync(
    path.join(directory, "package.json"),
    '{"name":"leftpad","main":"index.ts","types":"index.ts"}\n',
  );
  writeFileSync(
    path.join(workspacePath, "src", "cart.ts"),
    'import { pad } from "leftpad";\nexport class Cart { total = pad; }\n',
  );
  return { jsFile, tsFile };
}

function useTestProgram(workspacePath: string): void {
  const appConfigPath = path.join(workspacePath, "tsconfig.app.json");
  const appConfig = JSON.parse(readFileSync(appConfigPath, "utf8")) as { exclude?: string[] };
  appConfig.exclude = ["src/**/*.spec.ts"];
  writeFileSync(appConfigPath, `${JSON.stringify(appConfig, null, 2)}\n`);
  mkdirSync(path.join(workspacePath, "cfg"));
  writeFileSync(
    path.join(workspacePath, "cfg", "tsconfig.spec.json"),
    `${JSON.stringify(
      {
        compilerOptions: {
          strict: true,
          target: "ES2022",
          module: "ES2022",
          moduleResolution: "bundler",
          skipLibCheck: true,
        },
        include: ["../src/**/*.spec.ts"],
      },
      null,
      2,
    )}\n`,
  );
  const angularPath = path.join(workspacePath, "angular.json");
  const angular = JSON.parse(readFileSync(angularPath, "utf8")) as {
    projects: { shop: { architect: Record<string, unknown> } };
  };
  angular.projects.shop.architect.test = {
    builder: "@angular-devkit/build-angular:karma",
    options: { tsConfig: "cfg/tsconfig.spec.json" },
  };
  writeFileSync(angularPath, `${JSON.stringify(angular, null, 2)}\n`);
  writeFileSync(path.join(workspacePath, "src", "cart.spec.ts"), "export class CartSpec {}\n");
}

function setBuildOptions(workspacePath: string, options: Record<string, unknown>): void {
  const angularPath = path.join(workspacePath, "angular.json");
  const angular = JSON.parse(readFileSync(angularPath, "utf8")) as {
    projects: { shop: { architect: { build: { options: Record<string, unknown> } } } };
  };
  Object.assign(angular.projects.shop.architect.build.options, options);
  writeFileSync(angularPath, `${JSON.stringify(angular, null, 2)}\n`);
}

describe("MCP freshness and stale refusal", () => {
  it("refuses coverage after a TypeScript source changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({
          state: "stale",
          changedFiles: ["src/cart.ts"],
          diagnostics: [],
        }),
        coverage: generationCoverage("complete"),
        error: {
          code: "stale_index",
          message: staleMessage,
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after package-lock.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const lockPath = path.join(workspace.path, "package-lock.json");
    writeFileSync(lockPath, '{"name":"shop","lockfileVersion":3,"packages":{}}\n');
    await indexWorkspace(workspace.path);
    writeFileSync(lockPath, '{"name":"shop","lockfileVersion":3,"packages":{},"changed":true}\n');
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["package-lock.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after angular.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    const angularPath = path.join(workspace.path, "angular.json");
    const angular = JSON.parse(readFileSync(angularPath, "utf8")) as { version: number };
    angular.version = 2;
    writeFileSync(angularPath, `${JSON.stringify(angular, null, 2)}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["angular.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and warns after a stylesheet changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const stylesheet = path.join(workspace.path, "src", "styles.css");
    writeFileSync(stylesheet, "body { color: black; }\n");
    setBuildOptions(workspace.path, { styles: ["src/styles.css"] });
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "body { color: white; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 1,
        freshness: freshness({
          state: "fresh",
          changedFiles: [],
          diagnostics: [styleOrAssetWarning("src/styles.css")],
        }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and warns after an asset changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const assetDirectory = path.join(workspace.path, "src", "assets");
    mkdirSync(assetDirectory);
    const asset = path.join(assetDirectory, "logo.svg");
    writeFileSync(asset, "<svg></svg>\n");
    setBuildOptions(workspace.path, { assets: ["src/assets"] });
    await indexWorkspace(workspace.path);
    writeFileSync(asset, "<svg><!-- changed --></svg>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("src/assets/logo.svg")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after an external template changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const template = path.join(workspace.path, "src", "cart.html");
    writeFileSync(template, "<p>Cart</p>\n");
    await indexWorkspace(workspace.path);
    writeFileSync(template, "<p>Cart changed</p>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        freshness: { state: string; changedFiles: string[] };
        error: { code: string };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.html"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh after a file is added under node_modules", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const generationId = await indexWorkspace(workspace.path);
    const added = path.join(workspace.path, "node_modules", "added");
    mkdirSync(added, { recursive: true });
    writeFileSync(path.join(added, "index.js"), "module.exports = 1;\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh after an existing node_modules file is modified", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const installed = installLeftpad(workspace.path);
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(installed.jsFile, "module.exports = 2;\n");
    writeFileSync(installed.tsFile, "export const pad = 2;\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh after an existing node_modules file is deleted", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const installed = installLeftpad(workspace.path);
    const generationId = await indexWorkspace(workspace.path);
    rmSync(installed.jsFile);
    rmSync(installed.tsFile);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after tsconfig.app.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    const tsconfigPath = path.join(workspace.path, "tsconfig.app.json");
    const tsconfig = JSON.parse(readFileSync(tsconfigPath, "utf8")) as {
      compilerOptions: { target: string };
    };
    tsconfig.compilerOptions.target = "ES2023";
    writeFileSync(tsconfigPath, `${JSON.stringify(tsconfig, null, 2)}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string; message: string };
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.error.message).toBe(staleMessage);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["tsconfig.app.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after package.json changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    const packagePath = path.join(workspace.path, "package.json");
    const manifest = JSON.parse(readFileSync(packagePath, "utf8")) as { changed?: boolean };
    manifest.changed = true;
    writeFileSync(packagePath, `${JSON.stringify(manifest, null, 2)}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string; message: string };
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.error.message).toBe(staleMessage);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["package.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after a tracked TypeScript file is deleted", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    rmSync(path.join(workspace.path, "src", "cart.ts"));
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string; message: string };
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.error.message).toBe(staleMessage);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.ts"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage when a TypeScript file is added after indexing", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(workspace.path, "src", "extra.ts"), "export class Extra {}\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({
          state: "stale",
          changedFiles: ["src/extra.ts"],
          diagnostics: [],
        }),
        coverage: generationCoverage("complete"),
        error: {
          code: "stale_index",
          message: staleMessage,
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage when an html file is added under sourceRoot", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(workspace.path, "src", "page.html"), "<p>Page</p>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/page.html"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage when a workspace tsconfig is added", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(workspace.path, "tsconfig.extra.json"), "{}\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["tsconfig.extra.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("warns when a style file is added and stays fresh", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const theme = path.join(workspace.path, "src", "theme");
    mkdirSync(theme);
    writeFileSync(path.join(theme, "base.css"), "body { color: black; }\n");
    setBuildOptions(workspace.path, { styles: ["src/theme"] });
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(theme, "extra.css"), "h1 { color: navy; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("src/theme/extra.css")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("warns when an asset file is added and stays fresh", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const assetDirectory = path.join(workspace.path, "src", "assets");
    mkdirSync(assetDirectory);
    writeFileSync(path.join(assetDirectory, "logo.svg"), "<svg></svg>\n");
    setBuildOptions(workspace.path, { assets: ["src/assets"] });
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(assetDirectory, "mark.svg"), "<svg></svg>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("src/assets/mark.svg")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage when a TypeScript file under an asset input changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    setBuildOptions(workspace.path, { assets: [{ glob: "**/*.svg", input: "src" }] });
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 2; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.ts"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage when a TypeScript file is added under an asset input", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    setBuildOptions(workspace.path, { assets: [{ glob: "**/*.svg", input: "src" }] });
    await indexWorkspace(workspace.path);
    writeFileSync(path.join(workspace.path, "src", "extra.ts"), "export class Extra {}\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/extra.ts"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh when an extends base in node_modules changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const packageDir = path.join(workspace.path, "node_modules", "leftpad");
    mkdirSync(packageDir, { recursive: true });
    const base = path.join(packageDir, "tsconfig.json");
    writeFileSync(base, `${JSON.stringify({ compilerOptions: { strict: true } })}\n`);
    writeFileSync(
      path.join(workspace.path, "tsconfig.app.json"),
      `${JSON.stringify(
        {
          extends: "leftpad/tsconfig.json",
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
    );
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(base, `${JSON.stringify({ compilerOptions: { strict: false } })}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh when an extends base outside the workspace changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const outsideDir = path.join(
      path.dirname(workspace.path),
      `outside-${path.basename(workspace.path)}`,
    );
    mkdirSync(outsideDir);
    const base = path.join(outsideDir, "tsconfig.base.json");
    writeFileSync(base, `${JSON.stringify({ compilerOptions: { strict: true } })}\n`);
    const extendsPath = path.relative(workspace.path, base).replaceAll("\\", "/");
    writeFileSync(
      path.join(workspace.path, "tsconfig.app.json"),
      `${JSON.stringify(
        {
          extends: extendsPath,
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
    );
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(base, `${JSON.stringify({ compilerOptions: { strict: false } })}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
      rmSync(outsideDir, { recursive: true, force: true });
    }
  });

  it("stays fresh and warns after a component stylesheet changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const stylesheet = path.join(workspace.path, "src", "cart.component.scss");
    writeFileSync(stylesheet, "h1 { color: black; }\n");
    await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "h1 { color: navy; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        error?: { code: string };
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.error).toBeUndefined();
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([
        styleOrAssetWarning("src/cart.component.scss"),
      ]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("stays fresh and warns after a styleUrls target outside sourceRoot changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const brand = path.join(workspace.path, "brand");
    mkdirSync(brand);
    const stylesheet = path.join(brand, "cart.less");
    writeFileSync(stylesheet, "h1 { color: black; }\n");
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      [
        'import { Component } from "@angular/core";',
        '@Component({ selector: "app-cart", template: "<p></p>", styleUrls: ["../brand/cart.less"] })',
        "export class Cart {}",
        "",
      ].join("\n"),
    );
    await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "h1 { color: navy; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result) as {
        success: boolean;
        warningCount: number;
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("brand/cart.less")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after an in-workspace tsconfig extends base changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const configDirectory = path.join(workspace.path, "cfg");
    mkdirSync(configDirectory);
    writeFileSync(
      path.join(configDirectory, "tsconfig.base.json"),
      `${JSON.stringify({ compilerOptions: { strict: true }, include: ["src/**/*.ts"] }, null, 2)}\n`,
    );
    writeFileSync(
      path.join(configDirectory, "tsconfig.app.json"),
      `${JSON.stringify({ extends: "./tsconfig.base.json" }, null, 2)}\n`,
    );
    setBuildOptions(workspace.path, { tsConfig: "cfg/tsconfig.app.json" });
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(configDirectory, "tsconfig.base.json"),
      `${JSON.stringify({ compilerOptions: { strict: false }, include: ["src/**/*.ts"] }, null, 2)}\n`,
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["cfg/tsconfig.base.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("answers coverage as fresh after the changed workspace is indexed again", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );
    const generationId = await indexWorkspace(workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "coverage",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({ state: "fresh", changedFiles: [], diagnostics: [] }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 0,
          returned: 0,
          total: 0,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: false,
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("keeps the style warning when a stale-making file also changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const stylesheet = path.join(workspace.path, "src", "styles.css");
    writeFileSync(stylesheet, "body { color: black; }\n");
    setBuildOptions(workspace.path, { styles: [{ input: "src/styles.css" }] });
    await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "body { color: navy; }\n");
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 4; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        contractVersion: number;
        command: string;
        success: boolean;
        warningCount: number;
        error: { code: string; message: string; diagnostics: string[] };
        freshness: {
          state: string;
          isStale: boolean;
          changedFiles: string[];
          diagnostics: string[];
        };
      };
      expect(envelope.contractVersion).toBe(1);
      expect(envelope.command).toBe("coverage");
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.error.message).toBe(staleMessage);
      expect(envelope.error.diagnostics).toEqual([]);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.ts"]);
      expect(envelope.warningCount).toBe(1);
      expect(envelope.freshness.diagnostics).toEqual([styleOrAssetWarning("src/styles.css")]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after options.index html outside sourceRoot changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexHtml = path.join(workspace.path, "index.html");
    writeFileSync(indexHtml, "<!doctype html><html></html>\n");
    setBuildOptions(workspace.path, { index: "index.html" });
    await indexWorkspace(workspace.path);
    writeFileSync(indexHtml, "<!doctype html><html><body></body></html>\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string; message: string };
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.error.message).toBe(staleMessage);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["index.html"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses find_symbol after a TypeScript source changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart" },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
      }).toEqual({
        contractVersion: 1,
        command: "find_symbol",
        success: false,
        responseToolVersion: "0.00.01",
        warningCount: 0,
        freshness: freshness({
          state: "stale",
          changedFiles: ["src/cart.ts"],
          diagnostics: [],
        }),
        coverage: generationCoverage("complete"),
        error: {
          code: "stale_index",
          message: staleMessage,
          diagnostics: [],
        },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("returns style warnings from find_symbol and stays fresh", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const stylesheet = path.join(workspace.path, "src", "styles.css");
    writeFileSync(stylesheet, "body { color: black; }\n");
    setBuildOptions(workspace.path, { styles: ["src/styles.css"] });
    const generationId = await indexWorkspace(workspace.path);
    writeFileSync(stylesheet, "body { color: white; }\n");
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "find_symbol",
        arguments: { workspace: workspace.path, name: "Cart" },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = toolEnvelope(result);
      const body = envelope.freshness as { checkedAtUtc: string };
      expect(body.checkedAtUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      const symbols = (envelope.result as { symbols: { qualifiedName: string }[] }).symbols;
      expect(symbols.map((symbol) => symbol.qualifiedName)).toEqual(["Cart"]);
      expect({
        ...envelope,
        freshness: { ...body, checkedAtUtc: "TIMESTAMP" },
        result: { symbols: ["Cart"] },
      }).toEqual({
        contractVersion: 1,
        command: "find_symbol",
        success: true,
        responseToolVersion: "0.00.01",
        warningCount: 1,
        freshness: freshness({
          state: "fresh",
          changedFiles: [],
          diagnostics: [styleOrAssetWarning("src/styles.css")],
        }),
        coverage: generationCoverage("complete"),
        pagination: {
          offset: 0,
          limit: 100,
          returned: 1,
          total: 1,
          hasMore: false,
          nextOffset: null,
          snapshotId: generationId,
          continuationSupported: true,
        },
        result: { symbols: ["Cart"] },
      });
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after architect.test.options.tsConfig changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    useTestProgram(workspace.path);
    await indexWorkspace(workspace.path);
    const specConfig = path.join(workspace.path, "cfg", "tsconfig.spec.json");
    const parsed = JSON.parse(readFileSync(specConfig, "utf8")) as {
      compilerOptions: { target: string };
    };
    parsed.compilerOptions.target = "ES2023";
    writeFileSync(specConfig, `${JSON.stringify(parsed, null, 2)}\n`);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["cfg/tsconfig.spec.json"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("refuses coverage after a test-program file changes", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    useTestProgram(workspace.path);
    await indexWorkspace(workspace.path);
    writeFileSync(
      path.join(workspace.path, "src", "cart.spec.ts"),
      "export class CartSpec { total = 1; }\n",
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "coverage",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBe(true);
      const envelope = toolEnvelope(result) as {
        success: boolean;
        error: { code: string };
        freshness: { state: string; changedFiles: string[] };
      };
      expect(envelope.success).toBe(false);
      expect(envelope.error.code).toBe("stale_index");
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.spec.ts"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });
});
