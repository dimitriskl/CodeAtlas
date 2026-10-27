import {
  commandErrorEnvelope,
  coverageEnvelope,
  indexedCoverageEnvelope,
  staleIndexError,
  type ErrorEnvelope,
  type SuccessEnvelope,
} from "./envelope.js";
import { asCommandError } from "./errors.js";
import { readPublishedIndex } from "./generation-store.js";
import { resolveWorkspaceArgument } from "./index-command.js";

export type CoverageOutcome = {
  exitCode: 0 | 1 | 2 | 3;
  envelope: SuccessEnvelope | ErrorEnvelope;
};

/** Load the published generation and refuse it when a stale-making input changed. */
export function queryCoverage(workspacePath: string): CoverageOutcome {
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

export function executeCoverage(args: readonly string[], cwd: string): CoverageOutcome {
  try {
    return queryCoverage(
      resolveWorkspaceArgument(args, cwd, "Coverage accepts only --workspace <path>."),
    );
  } catch (error) {
    const commandError = asCommandError(error);
    return {
      exitCode: commandError.exitCode,
      envelope: commandErrorEnvelope("coverage", commandError),
    };
  }
}
