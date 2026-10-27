import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { coverageEnvelope, formatEnvelope } from "./envelope.js";
import { appVersion } from "./version.js";

/** In-process MCP server. The stdio executable connects this to stdin and stdout. */
export function createMcpServer(): McpServer {
  const server = new McpServer({
    name: "codeatlas-ng",
    version: appVersion,
  });
  server.registerTool(
    "coverage",
    {
      description:
        "Return the coverage boundary. With no index loaded, freshness is unknown and coverage is the static edition boundary.",
      inputSchema: {},
    },
    () => ({
      content: [{ type: "text", text: formatEnvelope(coverageEnvelope()) }],
    }),
  );
  return server;
}
