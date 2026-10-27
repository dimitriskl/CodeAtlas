import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

function readAppVersion(): string {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const manifest = JSON.parse(readFileSync(path.join(here, "..", "package.json"), "utf8")) as {
    version: string;
  };
  return manifest.version;
}

/**
 * JSON envelope contract. Independent of {@link schemaVersion} and {@link appVersion}.
 */
export const contractVersion = 1;

/**
 * Persisted index schema. Independent of {@link contractVersion} and {@link appVersion}.
 * `index` writes this schema into each generation.
 */
export const schemaVersion = 1;

/**
 * Application version (`x.xx.xx`). This is the package version and `responseToolVersion`.
 * Resolved from the manifest beside this module (`src/` in tests, `dist/` when installed).
 */
export const appVersion = readAppVersion();
