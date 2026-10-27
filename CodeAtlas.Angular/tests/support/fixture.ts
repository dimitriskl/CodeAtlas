import { cp, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { packageRoot } from "./run-cli.js";

const minimalFixture = path.join(packageRoot, "tests", "fixtures", "minimal-workspace");

export async function copyMinimalWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  const destination = await mkdtemp(path.join(tmpdir(), "codeatlas-ng-workspace-"));
  await cp(minimalFixture, destination, { recursive: true });
  return {
    path: destination,
    remove: () => rm(destination, { recursive: true, force: true }),
  };
}
