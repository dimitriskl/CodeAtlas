import { writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { runCli } from "./support/run-cli.js";

describe("codeatlas-ng index after a stale-making edit", () => {
  it("publishes a fresh generation instead of refusing", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const first = await runCli(["index", "--workspace", workspace.path]);
    if (first.exitCode !== 0) throw new Error(first.stderr);
    const firstId = (JSON.parse(first.stdout) as { result: { generationId: string } }).result
      .generationId;
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );

    try {
      // Act
      const result = await runCli(["index", "--workspace", workspace.path]);

      // Assert
      expect(result.exitCode).toBe(0);
      expect(result.stderr).toBe("");
      const envelope = JSON.parse(result.stdout) as {
        contractVersion: number;
        command: string;
        success: boolean;
        warningCount: number;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        result: { generationId: string };
      };
      expect(envelope.contractVersion).toBe(1);
      expect(envelope.command).toBe("index");
      expect(envelope.success).toBe(true);
      expect(envelope.warningCount).toBe(0);
      expect(envelope.freshness.state).toBe("fresh");
      expect(envelope.freshness.isStale).toBe(false);
      expect(envelope.freshness.changedFiles).toEqual([]);
      expect(envelope.result.generationId).not.toBe(firstId);
    } finally {
      await workspace.remove();
    }
  });
});
