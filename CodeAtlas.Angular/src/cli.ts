#!/usr/bin/env node
import { formatEnvelope, invalidArgumentsEnvelope } from "./envelope.js";
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
} else {
  const command = args[0] ?? null;
  process.stderr.write(formatEnvelope(invalidArgumentsEnvelope(command)));
  process.exitCode = 2;
}
