import { createHash, randomUUID } from "node:crypto";
import {
  closeSync,
  existsSync,
  fsyncSync,
  mkdirSync,
  openSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
  writeSync,
} from "node:fs";
import path from "node:path";
import Database from "better-sqlite3";
import { CommandError, nodeErrorCode } from "./errors.js";
import { appVersion, contractVersion, schemaVersion } from "./version.js";
import type { LoadedWorkspace, TemplateCoverage, TrackedFile } from "./workspace.js";
import {
  changedTrackedFiles,
  workspaceStorage,
  type WorkspaceStorage,
} from "./workspace-storage.js";

export type PublishedGeneration = {
  generationId: string;
  currentJson: string;
};

export type PublishedStatus = {
  generationId: string;
  schemaVersion: number;
  contractVersion: number;
  appVersion: string;
  templateCoverage: TemplateCoverage;
  counts: { projects: number; files: number; symbols: number };
  indexPath: string;
  canonical: string;
  changedFiles: string[];
};

type Manifest = {
  manifestVersion: 1;
  generationId: string;
  schemaVersion: number;
  workspace: string;
  artifacts: {
    "index.db": {
      length: number;
      sha256: string;
    };
  };
};

export function publishGeneration(workspace: LoadedWorkspace): PublishedGeneration {
  mkdirSync(workspace.directory, { recursive: true });
  const release = acquireWriterLock(path.join(workspace.directory, ".write.lock"));
  const generationId = randomUUID().replaceAll("-", "");
  const generationDir = path.join(workspace.directory, "generations", generationId);
  const temporary = path.join(workspace.directory, `current-${generationId}.tmp`);
  try {
    mkdirSync(generationDir, { recursive: true });
    const databasePath = path.join(generationDir, "index.db");
    writeDatabase(databasePath, generationId, workspace);
    const bytes = readFileSync(databasePath);
    const manifest: Manifest = {
      manifestVersion: 1,
      generationId,
      schemaVersion,
      workspace: workspace.identity,
      artifacts: {
        "index.db": {
          length: bytes.length,
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      },
    };
    writeFileSync(temporary, `${JSON.stringify(manifest, null, 2)}\n`);
    fsyncFile(temporary);
    renameSync(temporary, workspace.currentJson);
    return { generationId, currentJson: workspace.currentJson };
  } catch (error) {
    rmSync(temporary, { force: true });
    rmSync(generationDir, { recursive: true, force: true });
    throw error;
  } finally {
    release();
  }
}

function acquireWriterLock(lockPath: string): () => void {
  for (let attempt = 0; attempt < 2; attempt++) {
    let handle: number | undefined;
    try {
      handle = openSync(lockPath, "wx");
      writeSync(handle, `${process.pid}\n`);
      fsyncSync(handle);
      closeSync(handle);
      return () => {
        rmSync(lockPath, { force: true });
      };
    } catch (error) {
      if (handle !== undefined) {
        try {
          closeSync(handle);
        } catch {
          // The exclusive create failed after the handle was already closed.
        }
        rmSync(lockPath, { force: true });
      }
      if (nodeErrorCode(error) !== "EEXIST") throw error;
      const holder = readLockPid(lockPath);
      if (holder !== null && isProcessAlive(holder)) {
        throw new CommandError("index_busy", "Another index is publishing this workspace.");
      }
      rmSync(lockPath, { force: true });
    }
  }
  throw new CommandError("index_busy", "Another index is publishing this workspace.");
}

function readLockPid(lockPath: string): number | null {
  try {
    const text = readFileSync(lockPath, "utf8").trim();
    if (!/^[1-9]\d*$/.test(text)) return null;
    const pid = Number(text);
    return Number.isSafeInteger(pid) ? pid : null;
  } catch {
    return null;
  }
}

function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return nodeErrorCode(error) === "EPERM";
  }
}

/** The published generation, or null when this workspace has no current.json. */
export function readPublishedIndex(workspacePath: string): PublishedStatus | null {
  let storage: WorkspaceStorage;
  try {
    storage = workspaceStorage(workspacePath);
  } catch (error) {
    if (error instanceof CommandError && error.code === "workspace_not_found") return null;
    throw error;
  }
  if (!existsSync(storage.currentJson)) return null;
  const manifest = readManifest(storage);
  const databasePath = path.join(
    storage.directory,
    "generations",
    manifest.generationId,
    "index.db",
  );
  if (!existsSync(databasePath)) {
    throw new CommandError("invalid_index", "Snapshot artifact is missing: index.db.");
  }
  const bytes = readFileSync(databasePath);
  const digest = createHash("sha256").update(bytes).digest("hex");
  const artifact = manifest.artifacts["index.db"];
  if (bytes.length !== artifact.length || digest !== artifact.sha256) {
    throw new CommandError("invalid_index", "Snapshot artifact checksum mismatch: index.db.");
  }
  const database = new Database(databasePath, { readonly: true, fileMustExist: true });
  try {
    const metadata = readMetadata(database);
    if (metadata.get("schemaVersion") !== String(schemaVersion)) {
      throw new CommandError("unsupported_index", "Unsupported index schema.");
    }
    if (metadata.get("generationId") !== manifest.generationId) {
      throw new CommandError("invalid_index", "Snapshot and index identities disagree.");
    }
    const templateCoverage = metadata.get("templateCoverage");
    if (!isTemplateCoverage(templateCoverage)) {
      throw new CommandError("invalid_index", "Invalid template coverage in the generation.");
    }
    const storedContract = Number(metadata.get("contractVersion"));
    const storedApp = metadata.get("appVersion");
    if (!Number.isInteger(storedContract) || storedApp === undefined || storedApp.length === 0) {
      throw new CommandError("invalid_index", "Invalid versions in the generation.");
    }
    const files = database.prepare<[], TrackedFile>("SELECT path, sha256 FROM tracked_files").all();
    return {
      generationId: manifest.generationId,
      schemaVersion,
      contractVersion: storedContract,
      appVersion: storedApp,
      templateCoverage,
      counts: {
        projects: countRows(database, "projects"),
        files: countRows(database, "tracked_files"),
        symbols: countRows(database, "symbols"),
      },
      indexPath: storage.currentJson,
      canonical: storage.canonical,
      changedFiles: changedTrackedFiles(storage.canonical, files),
    };
  } finally {
    database.close();
  }
}

function readManifest(storage: WorkspaceStorage): Manifest {
  let parsed: unknown;
  try {
    parsed = JSON.parse(readFileSync(storage.currentJson, "utf8")) as unknown;
  } catch {
    throw new CommandError("invalid_index", "Invalid snapshot manifest JSON.");
  }
  if (!isRecord(parsed) || parsed.manifestVersion !== 1) {
    throw new CommandError("invalid_index", "Invalid snapshot manifest.");
  }
  const generationId = parsed.generationId;
  const artifacts = parsed.artifacts;
  if (
    typeof generationId !== "string" ||
    !/^[0-9a-f]{32}$/.test(generationId) ||
    !isRecord(artifacts)
  ) {
    throw new CommandError("invalid_index", "Invalid snapshot manifest.");
  }
  const index = artifacts["index.db"];
  if (
    !isRecord(index) ||
    typeof index.length !== "number" ||
    typeof index.sha256 !== "string" ||
    !/^[0-9a-f]{64}$/.test(index.sha256) ||
    parsed.schemaVersion !== schemaVersion ||
    parsed.workspace !== storage.identity
  ) {
    throw new CommandError("invalid_index", "Invalid snapshot manifest.");
  }
  return {
    manifestVersion: 1,
    generationId,
    schemaVersion,
    workspace: storage.identity,
    artifacts: { "index.db": { length: index.length, sha256: index.sha256 } },
  };
}

function readMetadata(database: Database.Database): Map<string, string> {
  const rows = database
    .prepare<[], { key: string; value: string }>("SELECT key, value FROM metadata")
    .all();
  return new Map(rows.map((row) => [row.key, row.value]));
}

function countRows(
  database: Database.Database,
  table: "projects" | "tracked_files" | "symbols",
): number {
  const sql = {
    projects: "SELECT COUNT(*) AS n FROM projects",
    tracked_files: "SELECT COUNT(*) AS n FROM tracked_files",
    symbols: "SELECT COUNT(*) AS n FROM symbols",
  }[table];
  const row = database.prepare<[], { n: number }>(sql).get();
  return row?.n ?? 0;
}

function isTemplateCoverage(value: string | undefined): value is TemplateCoverage {
  return value === "complete" || value === "partial" || value === "unknown";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function writeDatabase(
  databasePath: string,
  generationId: string,
  workspace: LoadedWorkspace,
): void {
  for (const file of workspace.files) assertStoredFilePath(file.path);
  for (const symbol of workspace.symbols) assertStoredFilePath(symbol.path);
  const database = new Database(databasePath);
  try {
    database.pragma("journal_mode = DELETE");
    database.exec(`
      CREATE TABLE metadata (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
      );
      CREATE TABLE projects (
        name TEXT PRIMARY KEY,
        root TEXT NOT NULL,
        source_root TEXT NOT NULL,
        project_type TEXT NOT NULL
      );
      CREATE TABLE tracked_files (
        path TEXT PRIMARY KEY,
        sha256 TEXT NOT NULL
      );
      CREATE TABLE symbols (
        id TEXT PRIMARY KEY,
        project TEXT NOT NULL,
        path TEXT NOT NULL,
        qualified_name TEXT NOT NULL,
        kind TEXT NOT NULL,
        line INTEGER NOT NULL,
        column INTEGER NOT NULL
      );
    `);
    const insert = database.transaction(() => {
      const metadata = database.prepare("INSERT INTO metadata (key, value) VALUES (?, ?)");
      metadata.run("generationId", generationId);
      metadata.run("schemaVersion", String(schemaVersion));
      metadata.run("contractVersion", String(contractVersion));
      metadata.run("appVersion", appVersion);
      metadata.run("templateCoverage", workspace.templateCoverage);
      const project = database.prepare(
        "INSERT INTO projects (name, root, source_root, project_type) VALUES (?, ?, ?, ?)",
      );
      for (const item of workspace.projects) {
        project.run(item.name, item.root, item.sourceRoot, item.projectType);
      }
      const file = database.prepare("INSERT INTO tracked_files (path, sha256) VALUES (?, ?)");
      for (const item of workspace.files) file.run(item.path, item.sha256);
      const symbol = database.prepare(
        "INSERT INTO symbols (id, project, path, qualified_name, kind, line, column) VALUES (?, ?, ?, ?, ?, ?, ?)",
      );
      for (const item of workspace.symbols) {
        symbol.run(
          item.id,
          item.project,
          item.path,
          item.qualifiedName,
          item.kind,
          item.line,
          item.column,
        );
      }
    });
    insert();
  } finally {
    database.close();
  }
  fsyncFile(databasePath);
}

function assertStoredFilePath(filePath: string): void {
  if (
    filePath.length === 0 ||
    filePath.startsWith("/") ||
    filePath.includes("\\") ||
    filePath.split("/").includes("..")
  ) {
    throw new CommandError("analysis_failed", `Refusing to store path ${filePath}.`);
  }
}

function fsyncFile(filePath: string): void {
  const handle = openSync(filePath, "r+");
  try {
    fsyncSync(handle);
  } finally {
    closeSync(handle);
  }
}
