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
import { changedTrackedFiles, loadWorkspace } from "./workspace.js";

export type IndexOutcome = {
  exitCode: 0 | 1 | 2;
  envelope: IndexSuccessEnvelope | ErrorEnvelope;
};

export function executeIndex(args: readonly string[], cwd: string): IndexOutcome {
  try {
    const loaded = loadWorkspace(parseWorkspaceArg(args, cwd));
    const published = publishGeneration(loaded);
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
        changedFiles: changedTrackedFiles(loaded.canonical, loaded.files),
        warningCount: 0,
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

function parseWorkspaceArg(args: readonly string[], cwd: string): string {
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
  throw new CommandError("invalid_arguments", "Index accepts only --workspace <path>.", 2);
}
