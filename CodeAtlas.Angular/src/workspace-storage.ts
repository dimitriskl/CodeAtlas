import { createHash } from "node:crypto";
import { readFileSync, realpathSync } from "node:fs";
import path from "node:path";
import { CommandError, nodeErrorCode, workspaceNotFoundMessage } from "./errors.js";

/** Folder identity from docs/MCP-CONTRACT.md (Storage naming). */
export type WorkspaceStorage = {
  canonical: string;
  identity: string;
  folder: string;
  directory: string;
  currentJson: string;
};

export function workspaceStorage(workspacePath: string): WorkspaceStorage {
  let canonical: string;
  try {
    canonical = realpathSync(path.resolve(workspacePath));
  } catch (error) {
    if (nodeErrorCode(error) === "ENOENT") {
      throw new CommandError("workspace_not_found", workspaceNotFoundMessage);
    }
    throw error;
  }
  let identity = canonical.replaceAll("\\", "/");
  if (identity.endsWith("/") && identity.length > 1) {
    const stripped = identity.slice(0, -1);
    if (!/^[A-Za-z]:$/.test(stripped)) identity = stripped;
  }
  if (process.platform === "win32") identity = identity.toUpperCase();
  const hash = createHash("sha256").update(identity, "utf8").digest("hex");
  const folder = `${path.basename(canonical)}-${hash}`;
  const directory = path.join(canonical, ".codeatlas", "workspaces", folder);
  return {
    canonical,
    identity,
    folder,
    directory,
    currentJson: path.join(directory, "current.json"),
  };
}

export function workspaceFile(canonical: string, storedPath: string): string {
  return path.join(canonical, ...storedPath.split("/"));
}

/** Ascending UTF-16 code-unit order, the same order as string comparison in JSON examples. */
export function compareCodeUnit(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

/** SHA-256 of the stored path's current bytes, or null when the file cannot be read. */
export function trackedContentHash(canonical: string, storedPath: string): string | null {
  try {
    return createHash("sha256")
      .update(readFileSync(workspaceFile(canonical, storedPath)))
      .digest("hex");
  } catch {
    return null;
  }
}
