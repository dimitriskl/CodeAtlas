import { describe, expect, it } from "vitest";
import { readPackageVersion, runCli } from "./support/run-cli.js";

describe("codeatlas-ng --version", () => {
  it("prints the package version", async () => {
    // Arrange
    const packageVersion = readPackageVersion();

    // Act
    const result = await runCli(["--version"]);

    // Assert
    expect(packageVersion).toBe("0.00.01");
    expect(result.exitCode).toBe(0);
    expect(result.stdout).toBe(`${packageVersion}\n`);
    expect(result.stderr).toBe("");
  });
});
