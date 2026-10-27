#!/usr/bin/env node
import { formatEnvelope, invalidArgumentsEnvelope } from "./envelope.js";
import { appVersion } from "./version.js";

const args = process.argv.slice(2);
if (args.length === 1 && args[0] === "--version") {
  process.stdout.write(`${appVersion}\n`);
} else {
  const command = args[0] ?? null;
  process.stderr.write(formatEnvelope(invalidArgumentsEnvelope(command)));
  process.exitCode = 2;
}
