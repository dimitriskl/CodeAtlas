import { readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { copyMinimalWorkspace } from "./support/fixture.js";
import { holdWriterLock } from "./support/lock-holder.js";
import { connectInProcess } from "./support/mcp-client.js";
import { runCli } from "./support/run-cli.js";
import { workspaceStorage } from "./support/storage-name.js";

function toolText(result: unknown): string {
  if (typeof result !== "object" || result === null || !("content" in result)) {
    throw new Error("index_status did not return content");
  }
  const content = result.content;
  if (!Array.isArray(content) || content.length === 0) {
    throw new Error("index_status did not return content");
  }
  const block: unknown = content[0];
  if (
    typeof block !== "object" ||
    block === null ||
    !("type" in block) ||
    block.type !== "text" ||
    !("text" in block) ||
    typeof block.text !== "string"
  ) {
    throw new Error("index_status did not return a text block");
  }
  return block.text;
}

describe("MCP index_status freshness", () => {
  it("reports changed files in code-unit order and does not refuse a stale generation", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const generationId = (JSON.parse(indexed.stdout) as { result: { generationId: string } }).result
      .generationId;
    writeFileSync(
      path.join(workspace.path, "src", "cart.ts"),
      "export class Cart { total = 1; }\n",
    );
    writeFileSync(
      path.join(workspace.path, "package.json"),
      '{"name":"shop","private":true,"dependencies":{"@angular/core":"19.2.0"},"changed":true}\n',
    );
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = JSON.parse(toolText(result)) as {
        command: string;
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
        result: { generationId: string };
        coverage: { limitations: string[] };
      };
      expect(envelope.command).toBe("index_status");
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["package.json", "src/cart.ts"]);
      expect(envelope.result.generationId).toBe(generationId);
      expect(envelope.coverage.limitations).toEqual([]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("reports a deleted tracked file as stale", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    rmSync(path.join(workspace.path, "src", "cart.ts"));
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = JSON.parse(toolText(result)) as {
        success: boolean;
        freshness: { state: string; isStale: boolean; changedFiles: string[] };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.freshness.state).toBe("stale");
      expect(envelope.freshness.isStale).toBe(true);
      expect(envelope.freshness.changedFiles).toEqual(["src/cart.ts"]);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("resolves a relative workspace path from the server current directory", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const generationId = (JSON.parse(indexed.stdout) as { result: { generationId: string } }).result
      .generationId;
    const relative = path.relative(process.cwd(), workspace.path);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: relative },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = JSON.parse(toolText(result)) as {
        success: boolean;
        result: { generationId: string; index: string };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.result.generationId).toBe(generationId);
      expect(envelope.result.index).toBe(workspaceStorage(workspace.path).currentJson);
    } finally {
      await connection.close();
      await workspace.remove();
    }
  });

  it("does not take or remove .write.lock", async () => {
    // Arrange
    const workspace = await copyMinimalWorkspace();
    const indexed = await runCli(["index", "--workspace", workspace.path]);
    if (indexed.exitCode !== 0) throw new Error(indexed.stderr);
    const generationId = (JSON.parse(indexed.stdout) as { result: { generationId: string } }).result
      .generationId;
    const lockPath = path.join(workspaceStorage(workspace.path).directory, ".write.lock");
    const holder = await holdWriterLock(lockPath);
    const connection = await connectInProcess();

    try {
      // Act
      const result = await connection.client.callTool({
        name: "index_status",
        arguments: { workspace: workspace.path },
      });

      // Assert
      expect(result.isError).toBeFalsy();
      const envelope = JSON.parse(toolText(result)) as {
        success: boolean;
        result: { generationId: string };
      };
      expect(envelope.success).toBe(true);
      expect(envelope.result.generationId).toBe(generationId);
      expect(readFileSync(lockPath, "utf8")).toBe(`${holder.pid}\n`);
    } finally {
      holder.stop();
      await connection.close();
      await workspace.remove();
    }
  });
});
