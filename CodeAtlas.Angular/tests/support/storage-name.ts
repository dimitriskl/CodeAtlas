import { createHash } from "node:crypto";
import { realpathSync } from "node:fs";
import path from "node:path";

/**
 * Workspace folder name from docs/MCP-CONTRACT.md (Storage naming).
 * This is the test oracle. It is not the product implementation.
 */
export function workspaceStorage(workspacePath: string): {
  canonical: string;
  identity: string;
  folder: string;
  directory: string;
  currentJson: string;
} {
  const canonical = realpathSync(path.resolve(workspacePath));
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
