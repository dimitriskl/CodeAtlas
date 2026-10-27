import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import {
  commandErrorEnvelope,
  coverageEnvelope,
  formatEnvelope,
  indexedCoverageEnvelope,
  indexStatusEmptyEnvelope,
  indexSuccessEnvelope,
  staleIndexError,
  type ErrorEnvelope,
  type SuccessEnvelope,
} from "./envelope.js";
import { asCommandError } from "./errors.js";
import { readPublishedIndex } from "./generation-store.js";
import { appVersion } from "./version.js";

/** Load the published generation and refuse it when a stale-making input changed. */
function queryCoverage(workspacePath: string): {
  exitCode: 0 | 1 | 2 | 3;
  envelope: SuccessEnvelope | ErrorEnvelope;
} {
  try {
    const status = readPublishedIndex(workspacePath);
    if (status === null) return { exitCode: 0, envelope: coverageEnvelope() };
    if (status.changedFiles.length > 0) {
      const error = staleIndexError({
        changedFiles: status.changedFiles,
        warnings: status.warnings,
        templateCoverage: status.templateCoverage,
      });
      return { exitCode: error.exitCode, envelope: commandErrorEnvelope("coverage", error) };
    }
    return {
      exitCode: 0,
      envelope: indexedCoverageEnvelope({
        generationId: status.generationId,
        templateCoverage: status.templateCoverage,
        changedFiles: status.changedFiles,
        warnings: status.warnings,
      }),
    };
  } catch (error) {
    const commandError = asCommandError(error);
    return {
      exitCode: commandError.exitCode,
      envelope: commandErrorEnvelope("coverage", commandError),
    };
  }
}

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
        "Return the coverage boundary. Without a workspace, freshness is unknown. With a workspace, load the published generation, hash tracked inputs, and refuse a stale index.",
      inputSchema: {
        workspace: z
          .string()
          .optional()
          .describe(
            "Path to the Angular workspace directory. Relative paths resolve from the server's current directory. Omit it when no index is loaded.",
          ),
      },
    },
    async ({ workspace }) => {
      if (workspace === undefined) {
        return {
          content: [{ type: "text", text: formatEnvelope(coverageEnvelope()) }],
        };
      }
      const outcome = queryCoverage(workspace);
      return {
        isError: outcome.exitCode !== 0,
        content: [{ type: "text", text: formatEnvelope(outcome.envelope) }],
      };
    },
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
                  warnings: status.warnings,
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
