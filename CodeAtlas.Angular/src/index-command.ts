import path from "node:path";
import {
  commandErrorEnvelope,
  indexSuccessEnvelope,
  type ErrorEnvelope,
  type IndexSuccessEnvelope,
} from "./envelope.js";
import { asCommandError, CommandError } from "./errors.js";
import { publishGeneration } from "./generation-store.js";
import { appVersion, contractVersion, schemaVersion } from "./version.js";
import { assessTrackedFreshness, loadWorkspace } from "./workspace.js";

export type IndexOutcome = {
  exitCode: 0 | 1 | 2 | 3;
  envelope: IndexSuccessEnvelope | ErrorEnvelope;
};

export function resolveWorkspaceArgument(
  args: readonly string[],
  cwd: string,
  usage: string,
): string {
  if (args.length === 0) return cwd;
  const workspace = args[1];
  if (
    args.length === 2 &&
    args[0] === "--workspace" &&
    workspace !== undefined &&
    workspace.length > 0
  ) {
    return path.resolve(cwd, workspace);
  }
  throw new CommandError("invalid_arguments", usage, 2);
}

export function executeIndex(args: readonly string[], cwd: string): IndexOutcome {
  try {
    const loaded = loadWorkspace(
      resolveWorkspaceArgument(args, cwd, "Index accepts only --workspace <path>."),
    );
    const published = publishGeneration(loaded);
    const assessed = assessTrackedFreshness(loaded.canonical, loaded.files);
    return {
      exitCode: 0,
      envelope: indexSuccessEnvelope({
        command: "index",
        generationId: published.generationId,
        schemaVersion,
        contractVersion,
        appVersion,
        counts: {
          projects: loaded.projects.length,
          files: loaded.files.length,
          symbols: loaded.symbols.length,
        },
        indexPath: published.currentJson,
        templateCoverage: loaded.templateCoverage,
        changedFiles: assessed.changedFiles,
        warnings: assessed.warnings,
      }),
    };
  } catch (error) {
    const commandError = asCommandError(error);
    return {
      exitCode: commandError.exitCode,
      envelope: commandErrorEnvelope("index", commandError),
    };
  }
}
