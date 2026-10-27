import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { packageRoot } from "./support/run-cli.js";

describe("codeatlas-ng package manifest", () => {
  it("names the package, version line, and both executables", () => {
    // Arrange
    const manifestPath = path.join(packageRoot, "package.json");

    // Act
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8")) as {
      name: string;
      version: string;
      type: string;
      license: string;
      engines: { node: string };
      bin: Record<string, string>;
    };

    // Assert
    expect(manifest.name).toBe("codeatlas-ng");
    expect(manifest.version).toBe("0.00.01");
    expect(manifest.type).toBe("module");
    expect(manifest.license).toBe("MIT");
    expect(manifest.engines.node).toBe(">=24");
    expect(manifest.bin).toEqual({
      "codeatlas-ng": "./dist/cli.js",
      "codeatlas-ng-mcp": "./dist/mcp.js",
    });
  });
});
