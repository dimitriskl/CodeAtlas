import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, realpathSync, statSync } from "node:fs";
import path from "node:path";
import ts from "typescript";
import { CommandError, workspaceNotFoundMessage } from "./errors.js";
import {
  compareCodeUnit,
  trackedContentHash,
  workspaceStorage,
  type WorkspaceStorage,
} from "./workspace-storage.js";

const styleExtensions = new Set([".css", ".scss", ".sass", ".less"]);
const skippedDirectories = new Set(["node_modules", ".codeatlas", ".git"]);

export type TemplateCoverage = "complete" | "partial" | "unknown";

export type ProjectRecord = {
  name: string;
  root: string;
  sourceRoot: string;
  projectType: string;
};

export type TrackedFile = {
  path: string;
  sha256: string;
};

export type ClassSymbol = {
  id: string;
  project: string;
  path: string;
  qualifiedName: string;
  kind: "class";
  line: number;
  column: number;
};

export type LoadedWorkspace = WorkspaceStorage & {
  templateCoverage: TemplateCoverage;
  projects: ProjectRecord[];
  files: TrackedFile[];
  symbols: ClassSymbol[];
};

export function loadWorkspace(workspacePath: string): LoadedWorkspace {
  const storage = workspaceStorage(workspacePath);
  const angularPath = path.join(storage.canonical, "angular.json");
  if (!existsSync(angularPath)) {
    throw new CommandError("workspace_not_found", workspaceNotFoundMessage);
  }
  const angular = readJsonFile(angularPath, "angular.json");
  if (!isRecord(angular) || !isRecord(angular.projects)) {
    throw new CommandError("workspace_not_found", workspaceNotFoundMessage);
  }

  const files = new Map<string, string>();
  track(files, storage.canonical, angularPath);
  const packagePath = path.join(storage.canonical, "package.json");
  const packageManifest = existsSync(packagePath)
    ? readJsonFile(packagePath, "package.json")
    : undefined;
  if (packageManifest !== undefined) track(files, storage.canonical, packagePath);
  const lockPath = path.join(storage.canonical, "package-lock.json");
  if (existsSync(lockPath)) track(files, storage.canonical, lockPath);
  for (const entry of readdirSync(storage.canonical)) {
    if (/^tsconfig.*\.json$/.test(entry))
      track(files, storage.canonical, path.join(storage.canonical, entry));
  }

  const projects: ProjectRecord[] = [];
  const symbols: ClassSymbol[] = [];
  for (const [name, value] of Object.entries(angular.projects)) {
    if (!isRecord(value)) {
      throw new CommandError(
        "analysis_failed",
        `Project ${name} in angular.json is not an object.`,
      );
    }
    const project = readProject(name, value);
    projects.push(project);
    const tsconfig = resolveTsConfig(storage.canonical, value);
    if (tsconfig !== undefined) {
      track(files, storage.canonical, tsconfig);
      symbols.push(...classesInProject(storage.canonical, project.name, tsconfig, files));
    }
    trackTemplates(storage.canonical, project, value, files);
    for (const entry of styleAndAssetEntries(value)) {
      trackOptionalPath(files, storage.canonical, entry);
    }
  }

  return {
    ...storage,
    templateCoverage: templateCoverageFor(angularMajor(packageManifest)),
    projects,
    files: [...files.entries()].map(([filePath, sha256]) => ({ path: filePath, sha256 })),
    symbols,
  };
}

function classesInProject(
  canonical: string,
  project: string,
  tsconfigPath: string,
  files: Map<string, string>,
): ClassSymbol[] {
  const config = ts.readConfigFile(tsconfigPath, ts.sys.readFile);
  if (config.error !== undefined) {
    throw new CommandError(
      "analysis_failed",
      `Could not read ${workspacePathOf(canonical, tsconfigPath)}.`,
    );
  }
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, path.dirname(tsconfigPath));
  const program = ts.createProgram({ rootNames: parsed.fileNames, options: parsed.options });
  const symbols: ClassSymbol[] = [];
  for (const sourceFile of program.getSourceFiles()) {
    if (sourceFile.isDeclarationFile) continue;
    let sourcePath: string;
    try {
      sourcePath = realpathSync(sourceFile.fileName);
    } catch {
      continue;
    }
    const relative = workspacePathOf(canonical, sourcePath);
    if (relative === null || relative.split("/").includes("node_modules")) continue;
    track(files, canonical, sourcePath);
    for (const statement of sourceFile.statements) {
      if (!ts.isClassDeclaration(statement) || statement.name === undefined) continue;
      const position = sourceFile.getLineAndCharacterOfPosition(
        statement.name.getStart(sourceFile, false),
      );
      const qualifiedName = statement.name.text;
      symbols.push({
        id: `${project}::${relative}#${qualifiedName}`,
        project,
        path: relative,
        qualifiedName,
        kind: "class",
        line: position.line + 1,
        column: position.character + 1,
      });
    }
  }
  return symbols;
}

function resolveTsConfig(canonical: string, raw: Record<string, unknown>): string | undefined {
  const configured = readTsConfigOption(raw);
  if (configured !== undefined) {
    const relative = normalizeRelative(configured);
    const absolute = path.join(canonical, ...relative.split("/").filter((part) => part.length > 0));
    if (!existsSync(absolute)) {
      throw new CommandError("analysis_failed", `TypeScript configuration not found: ${relative}.`);
    }
    return absolute;
  }
  const fallback = path.join(canonical, "tsconfig.json");
  return existsSync(fallback) ? fallback : undefined;
}

function readTsConfigOption(project: Record<string, unknown>): string | undefined {
  const architect = project.architect;
  if (architect === undefined) return undefined;
  if (!isRecord(architect)) {
    throw new CommandError("analysis_failed", "angular.json build options are invalid.");
  }
  const build = architect.build;
  if (build === undefined) return undefined;
  if (!isRecord(build)) {
    throw new CommandError("analysis_failed", "angular.json build options are invalid.");
  }
  const options = build.options;
  if (options === undefined) return undefined;
  if (!isRecord(options)) {
    throw new CommandError("analysis_failed", "angular.json build options are invalid.");
  }
  const tsConfig = options.tsConfig;
  if (tsConfig === undefined) return undefined;
  if (typeof tsConfig !== "string" || tsConfig.length === 0) {
    throw new CommandError("analysis_failed", "angular.json tsConfig must be a path.");
  }
  return tsConfig;
}

function readProject(name: string, project: Record<string, unknown>): ProjectRecord {
  const root = typeof project.root === "string" ? normalizeRelative(project.root) : "";
  return {
    name,
    root,
    sourceRoot:
      typeof project.sourceRoot === "string" ? normalizeRelative(project.sourceRoot) : root,
    projectType: typeof project.projectType === "string" ? project.projectType : "application",
  };
}

function templateCoverageFor(major: number | null): TemplateCoverage {
  if (major === null) return "unknown";
  if (major >= 17) return "complete";
  // Angular below 15 is refused by a later task. Until then the coverage stays unknown.
  if (major >= 15) return "partial";
  return "unknown";
}

function angularMajor(manifest: unknown): number | null {
  if (!isRecord(manifest)) return null;
  const dependencies = isRecord(manifest.dependencies) ? manifest.dependencies : undefined;
  const devDependencies = isRecord(manifest.devDependencies) ? manifest.devDependencies : undefined;
  const version = dependencies?.["@angular/core"] ?? devDependencies?.["@angular/core"];
  if (typeof version !== "string") return null;
  const match = /\d+/.exec(version);
  return match === null ? null : Number(match[0]);
}

/** Compare stored hashes. Style and asset edits become warnings; other tracked edits are stale. */
export function assessTrackedFreshness(
  canonical: string,
  files: readonly TrackedFile[],
): { changedFiles: string[]; warnings: string[] } {
  const roots = warningInputRoots(canonical);
  const changedFiles: string[] = [];
  const warningPaths: string[] = [];
  for (const file of files) {
    if (trackedContentHash(canonical, file.path) === file.sha256) continue;
    if (isWarningInput(file.path, roots)) warningPaths.push(file.path);
    else changedFiles.push(file.path);
  }
  changedFiles.sort(compareCodeUnit);
  warningPaths.sort(compareCodeUnit);
  return {
    changedFiles,
    warnings: warningPaths.map((filePath) => `Style or asset changed: ${filePath}.`),
  };
}

function isWarningInput(filePath: string, roots: readonly string[]): boolean {
  if (styleExtensions.has(path.posix.extname(filePath).toLowerCase())) return true;
  return roots.some((root) => filePath === root || filePath.startsWith(`${root}/`));
}

function warningInputRoots(canonical: string): string[] {
  try {
    const angular = JSON.parse(
      readFileSync(path.join(canonical, "angular.json"), "utf8"),
    ) as unknown;
    if (!isRecord(angular) || !isRecord(angular.projects)) return [];
    const roots: string[] = [];
    for (const project of Object.values(angular.projects)) {
      if (!isRecord(project)) continue;
      for (const entry of styleAndAssetEntries(project)) {
        const relative = normalizeRelative(entry);
        if (isSafeRelative(relative)) roots.push(relative);
      }
    }
    return roots;
  } catch {
    return [];
  }
}

function trackTemplates(
  canonical: string,
  project: ProjectRecord,
  raw: Record<string, unknown>,
  files: Map<string, string>,
): void {
  if (project.sourceRoot.length > 0) {
    const absolute = path.join(canonical, ...project.sourceRoot.split("/"));
    if (existsSync(absolute)) {
      const real = realpathSync(absolute);
      if (statSync(real).isDirectory()) {
        walkFiles(real, (file) => {
          if (file.endsWith(".html")) track(files, canonical, file);
        });
      }
    }
  }
  for (const indexPath of indexHtmlEntries(raw)) trackOptionalPath(files, canonical, indexPath);
}

function styleAndAssetEntries(project: Record<string, unknown>): string[] {
  const entries: string[] = [];
  for (const options of architectOptions(project)) {
    entries.push(...readInputList(options.styles, "angular.json styles"));
    entries.push(...readInputList(options.assets, "angular.json assets"));
  }
  return entries;
}

function indexHtmlEntries(project: Record<string, unknown>): string[] {
  const paths: string[] = [];
  for (const options of architectOptions(project)) {
    const index = options.index;
    if (typeof index === "string") paths.push(index);
    else if (isRecord(index) && typeof index.input === "string") paths.push(index.input);
    else if (index !== undefined) {
      throw new CommandError("analysis_failed", "angular.json index must be a path.");
    }
  }
  return paths;
}

function architectOptions(project: Record<string, unknown>): Record<string, unknown>[] {
  const architect = project.architect;
  if (architect === undefined) return [];
  if (!isRecord(architect)) {
    throw new CommandError("analysis_failed", "angular.json build options are invalid.");
  }
  const options: Record<string, unknown>[] = [];
  for (const target of Object.values(architect)) {
    if (!isRecord(target) || target.options === undefined) continue;
    if (!isRecord(target.options)) {
      throw new CommandError("analysis_failed", "angular.json build options are invalid.");
    }
    options.push(target.options);
  }
  return options;
}

function readInputList(value: unknown, label: string): string[] {
  if (value === undefined) return [];
  if (!Array.isArray(value)) {
    throw new CommandError("analysis_failed", `${label} must be an array.`);
  }
  const paths: string[] = [];
  for (const entry of value) {
    if (typeof entry === "string" && entry.length > 0) {
      paths.push(entry);
      continue;
    }
    if (isRecord(entry) && typeof entry.input === "string" && entry.input.length > 0) {
      paths.push(entry.input);
      continue;
    }
    throw new CommandError("analysis_failed", `${label} entries must be paths.`);
  }
  return paths;
}

function trackOptionalPath(files: Map<string, string>, canonical: string, raw: string): void {
  const relative = normalizeRelative(raw);
  if (!isSafeRelative(relative)) return;
  const absolute = path.join(canonical, ...relative.split("/"));
  if (!existsSync(absolute)) return;
  let real: string;
  try {
    real = realpathSync(absolute);
  } catch {
    return;
  }
  const info = statSync(real);
  if (info.isDirectory()) walkFiles(real, (file) => track(files, canonical, file));
  else if (info.isFile()) track(files, canonical, real);
}

function walkFiles(directory: string, visit: (file: string) => void): void {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (skippedDirectories.has(entry.name)) continue;
    const full = path.join(directory, entry.name);
    if (entry.isSymbolicLink()) {
      try {
        if (statSync(full).isFile()) visit(full);
      } catch {
        // A broken symlink is not a tracked input.
      }
      continue;
    }
    if (entry.isDirectory()) walkFiles(full, visit);
    else if (entry.isFile()) visit(full);
  }
}

function isSafeRelative(relative: string): boolean {
  if (relative.length === 0) return false;
  const parts = relative.split("/");
  return !parts.includes("..") && !parts.includes("node_modules");
}

function track(files: Map<string, string>, canonical: string, absolute: string): void {
  const relative = workspacePathOf(canonical, absolute);
  if (relative === null || files.has(relative)) return;
  files.set(relative, sha256File(absolute));
}

function workspacePathOf(canonical: string, absolute: string): string | null {
  const relative = path.relative(canonical, absolute);
  if (relative.length === 0 || relative.startsWith("..") || path.isAbsolute(relative)) return null;
  const parts = relative.split(/[\\/]/);
  if (parts.includes("node_modules")) return null;
  return parts.join("/");
}

function normalizeRelative(value: string): string {
  const slashed = value.replaceAll("\\", "/").replace(/^\.\//, "").replace(/\/$/, "");
  return slashed === "." ? "" : slashed;
}

function sha256File(filePath: string): string {
  return createHash("sha256").update(readFileSync(filePath)).digest("hex");
}

function readJsonFile(filePath: string, label: string): unknown {
  try {
    return JSON.parse(readFileSync(filePath, "utf8")) as unknown;
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new CommandError("analysis_failed", `${label} is not valid JSON.`);
    }
    throw error;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
