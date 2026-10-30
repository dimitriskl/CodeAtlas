import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import path from "node:path";
import { createMcpServer } from "../../src/index.js";
import { packageRoot } from "./run-cli.js";

export async function connectInProcess(): Promise<{
  client: Client;
  close: () => Promise<void>;
}> {
  const server = createMcpServer();
  const client = new Client({ name: "codeatlas-ng-test", version: "0.00.01" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await server.connect(serverTransport);
  await client.connect(clientTransport);
  return {
    client,
    close: async () => {
      await client.close();
      await server.close();
    },
  };
}

export async function connectStdio(): Promise<{
  client: Client;
  close: () => Promise<void>;
}> {
  const client = new Client({ name: "codeatlas-ng-test", version: "0.00.01" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(packageRoot, "dist", "mcp.js")],
    cwd: packageRoot,
    stderr: "pipe",
  });
  await client.connect(transport);
  return {
    client,
    close: async () => {
      await client.close();
    },
  };
}
