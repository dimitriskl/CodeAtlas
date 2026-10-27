import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import {
  commandErrorEnvelope,
  coverageEnvelope,
  formatEnvelope,
  indexStatusEmptyEnvelope,
  indexSuccessEnvelope,
} from "./envelope.js";
import { asCommandError } from "./errors.js";
import { readPublishedIndex } from "./generation-store.js";
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
  server.registerTool(
    "index_status",
    {
      description:
        "Report the published generation's id, versions, project, file and symbol counts, and freshness. When the workspace has no index, succeed with an empty result and freshness unknown.",
      inputSchema: {
        workspace: z.string().describe("Path to the Angular workspace directory."),
      },
    },
    async ({ workspace }) => {
      try {
        const status = readPublishedIndex(workspace);
        if (status === null) {
          return {
            content: [{ type: "text", text: formatEnvelope(indexStatusEmptyEnvelope()) }],
          };
        }
        return {
          content: [
            {
              type: "text",
              text: formatEnvelope(
                indexSuccessEnvelope({
                  command: "index_status",
                  generationId: status.generationId,
                  schemaVersion: status.schemaVersion,
                  contractVersion: status.contractVersion,
                  appVersion: status.appVersion,
                  counts: status.counts,
                  indexPath: status.indexPath,
                  templateCoverage: status.templateCoverage,
                  changedFiles: status.changedFiles,
                  warningCount: 0,
                }),
              ),
            },
          ],
        };
      } catch (error) {
        return {
          isError: true,
          content: [
            {
              type: "text",
              text: formatEnvelope(commandErrorEnvelope("index_status", asCommandError(error))),
            },
          ],
        };
      }
    },
  );
  return server;
}
