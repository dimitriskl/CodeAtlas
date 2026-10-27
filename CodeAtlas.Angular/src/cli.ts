#!/usr/bin/env node
import { CommandError } from "./command-error.js";
import { runConfigureAi } from "./configure-ai.js";
import { formatEnvelope, invalidArgumentsEnvelope, type ErrorEnvelope } from "./envelope.js";
import { appVersion, contractVersion } from "./version.js";

const args = process.argv.slice(2);
if (args.length === 1 && args[0] === "--version") {
  process.stdout.write(`${appVersion}\n`);
} else if (args[0] === "configure-ai") {
  try {
    const output = await runConfigureAi(args.slice(1));
    process.stdout.write(`${JSON.stringify(output, null, 2)}\n`);
  } catch (error) {
    const failure =
      error instanceof CommandError
        ? error
        : new CommandError("analysis_failed", "configure-ai failed.", 1);
    process.stderr.write(formatEnvelope(commandFailure("configure-ai", failure)));
    process.exitCode = failure.exitCode;
  }
} else {
  const command = args[0] ?? null;
  process.stderr.write(formatEnvelope(invalidArgumentsEnvelope(command)));
  process.exitCode = 2;
}

function commandFailure(command: string, error: CommandError): ErrorEnvelope {
  return {
    contractVersion,
    command,
    success: false,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: {
      code: error.code,
      message: error.message,
      diagnostics: [],
    },
  };
}
