import type { Coverage } from "./coverage.js";
import type { Freshness } from "./envelope.js";

export type CommandErrorDetails = {
  freshness?: Freshness | null;
  coverage?: Coverage | null;
  warningCount?: number;
};

export class CommandError extends Error {
  readonly code: string;
  readonly exitCode: 1 | 2 | 3;
  readonly diagnostics: readonly string[];
  readonly freshness: Freshness | null;
  readonly coverage: Coverage | null;
  readonly warningCount: number;

  constructor(
    code: string,
    message: string,
    exitCode: 1 | 2 | 3 = 1,
    diagnostics: readonly string[] = [],
    details: CommandErrorDetails = {},
  ) {
    super(message);
    this.name = "CommandError";
    this.code = code;
    this.exitCode = exitCode;
    this.diagnostics = diagnostics;
    this.freshness = details.freshness ?? null;
    this.coverage = details.coverage ?? null;
    this.warningCount = details.warningCount ?? 0;
  }
}

export const workspaceNotFoundMessage =
  "No Angular workspace configuration (angular.json) was found.";

export function nodeErrorCode(error: unknown): string | undefined {
  if (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    typeof error.code === "string"
  ) {
    return error.code;
  }
  return undefined;
}

export function asCommandError(error: unknown): CommandError {
  if (error instanceof CommandError) return error;
  const message = error instanceof Error ? error.message : "Index failed.";
  return new CommandError("analysis_failed", message);
}
