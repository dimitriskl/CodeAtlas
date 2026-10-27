#!/usr/bin/env node
import { runConfigureAi } from "./configure-ai.js";
import { commandErrorEnvelope, formatEnvelope, invalidArgumentsEnvelope } from "./envelope.js";
import { CommandError } from "./errors.js";
import { executeIndex } from "./index-command.js";
import { appVersion } from "./version.js";

const args = process.argv.slice(2);
if (args.length === 1 && args[0] === "--version") {
  process.stdout.write(`${appVersion}\n`);
} else if (args[0] === "index") {
  const outcome = executeIndex(args.slice(1), process.cwd());
  const text = formatEnvelope(outcome.envelope);
  if (outcome.exitCode === 0) {
    process.stdout.write(text);
  } else {
    process.stderr.write(text);
    process.exitCode = outcome.exitCode;
  }
} else if (args[0] === "configure-ai") {
  try {
    const output = await runConfigureAi(args.slice(1));
    process.stdout.write(formatEnvelope(output));
  } catch (error) {
    const failure =
      error instanceof CommandError
        ? error
        : new CommandError("analysis_failed", "configure-ai failed.", 1);
    process.stderr.write(formatEnvelope(commandErrorEnvelope("configure-ai", failure)));
    process.exitCode = failure.exitCode;
  }
} else {
  const command = args[0] ?? null;
  process.stderr.write(formatEnvelope(invalidArgumentsEnvelope(command)));
  process.exitCode = 2;
}
