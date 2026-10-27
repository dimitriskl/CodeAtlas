export class CommandError extends Error {
  readonly code: string;
  readonly exitCode: 1 | 2;
  readonly diagnostics: readonly string[];

  constructor(
    code: string,
    message: string,
    exitCode: 1 | 2 = 1,
    diagnostics: readonly string[] = [],
  ) {
    super(message);
    this.name = "CommandError";
    this.code = code;
    this.exitCode = exitCode;
    this.diagnostics = diagnostics;
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
