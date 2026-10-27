export class CommandError extends Error {
  readonly code: string;
  readonly exitCode: number;

  constructor(code: string, message: string, exitCode: number) {
    super(message);
    this.name = "CommandError";
    this.code = code;
    this.exitCode = exitCode;
  }
}
