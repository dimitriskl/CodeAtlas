import { describe, expect, it } from "vitest";
import { runCli } from "./support/run-cli.js";

describe("codeatlas-ng unknown command", () => {
  it("exits 2 with an invalid_arguments JSON error on stderr", async () => {
    // Arrange
    const command = "not-a-command";

    // Act
    const result = await runCli([command]);

    // Assert
    expect(result.exitCode).toBe(2);
    expect(result.stdout).toBe("");
    expect(JSON.parse(result.stderr)).toEqual({
      contractVersion: 1,
      command,
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
