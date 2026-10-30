import { describe, expect, it } from "vitest";
import { readPackageVersion, runCli } from "./support/run-cli.js";

describe("codeatlas-ng with no command", () => {
  it("exits 2 with invalid_arguments and a null command", async () => {
    // Arrange
    const packageVersion = readPackageVersion();

    // Act
    const result = await runCli([]);

    // Assert
    expect(packageVersion).toBe("0.00.01");
    expect(result.exitCode).toBe(2);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual({
      contractVersion: 1,
      command: null,
      success: false,
      responseToolVersion: packageVersion,
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
