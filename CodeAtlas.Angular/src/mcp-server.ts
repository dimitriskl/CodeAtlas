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
import { executeFindSymbol } from "./find-symbol.js";
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
  server.registerTool(
    "find_symbol",
    {
      description:
        "Find indexed TypeScript symbols by exact name or qualified name. Optional kind and project filters are exact. Pagination continues with snapshotId.",
      inputSchema: {
        workspace: z.string().describe("Path to the Angular workspace directory."),
        name: z.string().describe("Exact symbol name or qualified name."),
        kind: z.string().optional().describe("Exact symbol kind. Omit to allow every kind."),
        project: z.string().optional().describe("Exact angular.json project name."),
        offset: z.number().optional().describe("First matching row. Defaults to 0."),
        limit: z.number().optional().describe("Page size. Defaults to 100. Maximum 1000."),
        snapshotId: z
          .string()
          .optional()
          .describe(
            "Generation id from a previous page. Required when offset is greater than zero.",
          ),
      },
    },
    async (args) => {
      const envelope = executeFindSymbol(args);
      if (!envelope.success) {
        return {
          isError: true,
          content: [{ type: "text", text: formatEnvelope(envelope) }],
        };
      }
      return {
        content: [{ type: "text", text: formatEnvelope(envelope) }],
      };
    },
  );
  return server;
}
