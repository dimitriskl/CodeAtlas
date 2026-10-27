import { describe, expect, it } from "vitest";
import { connectStdio } from "./support/mcp-client.js";

describe("codeatlas-ng-mcp stdio", () => {
  it("completes the handshake and lists coverage and index_status", async () => {
    // Arrange
    const connection = await connectStdio();

    try {
      // Act
      const listed = await connection.client.listTools();

      // Assert
      expect(listed.tools.map((tool) => tool.name)).toEqual(["coverage", "index_status"]);
    } finally {
      await connection.close();
    }
  });
});
