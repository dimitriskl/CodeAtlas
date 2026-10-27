import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, realpathSync } from "node:fs";
import path from "node:path";
import ts from "typescript";
import { CommandError, workspaceNotFoundMessage } from "./errors.js";
import {
  changedTrackedFiles,
  workspaceStorage,
  type WorkspaceStorage,
} from "./workspace-storage.js";

export { changedTrackedFiles };

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
    if (tsconfig === undefined) continue;
    track(files, storage.canonical, tsconfig);
    symbols.push(...classesInProject(storage.canonical, project.name, tsconfig, files));
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
