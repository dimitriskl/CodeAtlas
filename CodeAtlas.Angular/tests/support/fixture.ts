import { cp, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { packageRoot } from "./run-cli.js";

const fixtures = path.join(packageRoot, "tests", "fixtures");

export async function copyMinimalWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  return copyWorkspace("minimal-workspace");
}

export async function copySymbolsWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  return copyWorkspace("symbols-workspace");
}

export async function copyDeclarationsWorkspace(): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  return copyWorkspace("declarations-workspace");
}

async function copyWorkspace(name: string): Promise<{
  path: string;
  remove: () => Promise<void>;
}> {
  const destination = await mkdtemp(path.join(tmpdir(), "codeatlas-ng-workspace-"));
  await cp(path.join(fixtures, name), destination, { recursive: true });
  return {
    path: destination,
    remove: () => rm(destination, { recursive: true, force: true }),
  };
}
