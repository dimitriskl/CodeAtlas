#!/usr/bin/env node
import { executeCoverage } from "./coverage-command.js";
import {
  formatEnvelope,
  invalidArgumentsEnvelope,
  type ErrorEnvelope,
  type IndexSuccessEnvelope,
  type SuccessEnvelope,
} from "./envelope.js";
import { executeIndex } from "./index-command.js";
import { appVersion } from "./version.js";

const args = process.argv.slice(2);
if (args.length === 1 && args[0] === "--version") {
  process.stdout.write(`${appVersion}\n`);
} else if (args[0] === "index") {
  writeOutcome(executeIndex(args.slice(1), process.cwd()));
} else if (args[0] === "coverage") {
  writeOutcome(executeCoverage(args.slice(1), process.cwd()));
} else {
  const command = args[0] ?? null;
  process.stderr.write(formatEnvelope(invalidArgumentsEnvelope(command)));
  process.exitCode = 2;
}

function writeOutcome(outcome: {
  exitCode: number;
  envelope: SuccessEnvelope | IndexSuccessEnvelope | ErrorEnvelope;
}): void {
  const text = formatEnvelope(outcome.envelope);
  if (outcome.exitCode === 0) {
    process.stdout.write(text);
  } else {
    process.stderr.write(text);
    process.exitCode = outcome.exitCode;
  }
}
