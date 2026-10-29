import { coverageForGeneration, staticCoverage, type Coverage } from "./coverage.js";
import { CommandError } from "./errors.js";
import { appVersion, contractVersion } from "./version.js";

export type Freshness = {
  state: "fresh" | "stale" | "unknown";
  checkedAtUtc: string;
  isStale: boolean;
  changedFiles: string[];
  scope: string;
  inputCoverage: {
    status: string;
    scope: string;
    issueCount: number;
    reasonCodes: string[];
  };
  diagnostics: string[];
};

export type Pagination = {
  offset: number;
  limit: number;
  returned: number;
  total: number;
  hasMore: boolean;
  nextOffset: number | null;
  snapshotId: string | null;
  continuationSupported: boolean;
};

export type EnvelopeError = {
  code: string;
  message: string;
  diagnostics: string[];
};

export type SuccessEnvelope = {
  contractVersion: number;
  command: string;
  success: true;
  responseToolVersion: string;
  warningCount: number;
  freshness: Freshness;
  coverage: Coverage;
  pagination: Pagination;
};

export type IndexCounts = {
  projects: number;
  files: number;
  symbols: number;
};

export type IndexResult = {
  generationId: string | null;
  schemaVersion: number | null;
  contractVersion: number;
  appVersion: string | null;
  counts: IndexCounts;
  index: string | null;
};

export type IndexSuccessEnvelope = SuccessEnvelope & {
  result: IndexResult;
};

export type FindSymbolHit = {
  id: string;
  kind: string;
  qualifiedName: string;
  project: string;
  path: string;
  line: number;
  column: number;
  signatures: string[];
};

export type FindSymbolEnvelope = SuccessEnvelope & {
  result: {
    symbols: FindSymbolHit[];
  };
};

export type FindReferenceHit = {
  targetId: string;
  kind: string;
  project: string;
  path: string;
  line: number;
  column: number;
};

export type FindReferencesEnvelope = SuccessEnvelope & {
  result: {
    references: FindReferenceHit[];
  };
};

export type ErrorEnvelope = {
  contractVersion: number;
  command: string | null;
  success: false;
  responseToolVersion: string;
  warningCount: number;
  freshness: Freshness | null;
  coverage: Coverage | null;
  error: EnvelopeError;
};

/** Freshness when no index has been loaded. Unknown is not stale. */
export function unknownFreshness(now: Date = new Date()): Freshness {
  return {
    state: "unknown",
    checkedAtUtc: now.toISOString(),
    isStale: false,
    changedFiles: [],
    scope: "tracked-input-content-hashes",
    inputCoverage: {
      status: "unknown",
      scope: "no-index",
      issueCount: 0,
      reasonCodes: [],
    },
    diagnostics: [],
  };
}

export function emptyPagination(snapshotId: string | null): Pagination {
  return {
    offset: 0,
    limit: 0,
    returned: 0,
    total: 0,
    hasMore: false,
    nextOffset: null,
    snapshotId,
    continuationSupported: false,
  };
}

/**
 * Freshness of a generation whose tracked file hashes were just compared.
 * Style and asset warnings stay out of `changedFiles`, so they do not make the index stale.
 */
export function trackedFreshness(
  changedFiles: readonly string[],
  now: Date = new Date(),
  warnings: readonly string[] = [],
): Freshness {
  const stale = changedFiles.length > 0;
  return {
    state: stale ? "stale" : "fresh",
    checkedAtUtc: now.toISOString(),
    isStale: stale,
    changedFiles: [...changedFiles],
    scope: "tracked-input-content-hashes",
    inputCoverage: {
      status: "complete",
      scope: "tracked-input-content-hashes",
      issueCount: 0,
      reasonCodes: [],
    },
    diagnostics: [...warnings],
  };
}

export function coverageEnvelope(now: Date = new Date()): SuccessEnvelope {
  return {
    contractVersion,
    command: "coverage",
    success: true,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: unknownFreshness(now),
    coverage: staticCoverage(),
    pagination: emptyPagination(null),
  };
}

export function indexSuccessEnvelope(input: {
  command: "index" | "index_status";
  generationId: string;
  schemaVersion: number;
  contractVersion: number;
  appVersion: string;
  counts: IndexCounts;
  indexPath: string;
  templateCoverage: Coverage["templateCoverage"];
  changedFiles: readonly string[];
  warnings: readonly string[];
  now?: Date;
}): IndexSuccessEnvelope {
  return {
    contractVersion,
    command: input.command,
    success: true,
    responseToolVersion: appVersion,
    warningCount: input.warnings.length,
    freshness: trackedFreshness(input.changedFiles, input.now, input.warnings),
    coverage: coverageForGeneration(input.templateCoverage),
    pagination: emptyPagination(input.generationId),
    result: {
      generationId: input.generationId,
      schemaVersion: input.schemaVersion,
      contractVersion: input.contractVersion,
      appVersion: input.appVersion,
      counts: input.counts,
      index: input.indexPath,
    },
  };
}

/** `index_status` when the workspace has no current.json. Nothing is written. */
export function indexStatusEmptyEnvelope(now: Date = new Date()): IndexSuccessEnvelope {
  return {
    contractVersion,
    command: "index_status",
    success: true,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: unknownFreshness(now),
    coverage: staticCoverage(),
    pagination: emptyPagination(null),
    result: {
      generationId: null,
      schemaVersion: null,
      contractVersion,
      appVersion: null,
      counts: { projects: 0, files: 0, symbols: 0 },
      index: null,
    },
  };
}

export type ConfigureAiSuccessEnvelope = {
  ai: "enabled";
  connection: "tested";
  provider: string;
  model: string;
  effort: string | null;
  endpoint: string;
  settings: string;
  credentialEnvironmentVariable: string | null;
  credentialStore: string | null;
  credentialWarning: null;
  message: string;
  contractVersion: number;
  command: "configure-ai";
  success: true;
  responseToolVersion: string;
  warningCount: number;
  freshness: null;
  coverage: null;
};

/** configure-ai success. Freshness and coverage stay null; no project source was sent. */
export function configureAiSuccessEnvelope(result: {
  provider: string;
  model: string;
  effort: string | null;
  endpoint: string;
  settings: string;
  credentialEnvironmentVariable: string | null;
  credentialStore: string | null;
}): ConfigureAiSuccessEnvelope {
  return {
    ai: "enabled",
    connection: "tested",
    provider: result.provider,
    model: result.model,
    effort: result.effort,
    endpoint: result.endpoint,
    settings: result.settings,
    credentialEnvironmentVariable: result.credentialEnvironmentVariable,
    credentialStore: result.credentialStore,
    credentialWarning: null,
    message: "Connection passed and options saved. No project source was sent.",
    contractVersion,
    command: "configure-ai",
    success: true,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: null,
    coverage: null,
  };
}

/** Coverage of a loaded generation. `snapshotId` is that generation; the page itself stays empty. */
export function indexedCoverageEnvelope(input: {
  generationId: string;
  templateCoverage: Coverage["templateCoverage"];
  changedFiles: readonly string[];
  warnings: readonly string[];
  now?: Date;
}): SuccessEnvelope {
  return {
    contractVersion,
    command: "coverage",
    success: true,
    responseToolVersion: appVersion,
    warningCount: input.warnings.length,
    freshness: trackedFreshness(input.changedFiles, input.now, input.warnings),
    coverage: coverageForGeneration(input.templateCoverage),
    pagination: emptyPagination(input.generationId),
  };
}

export const staleIndexMessage = "The saved index is stale.";

/** Query refusal. `index` and `index_status` report staleness and do not use this. */
export function staleIndexError(input: {
  changedFiles: readonly string[];
  warnings: readonly string[];
  templateCoverage: Coverage["templateCoverage"];
}): CommandError {
  return new CommandError("stale_index", staleIndexMessage, 3, [], {
    freshness: trackedFreshness(input.changedFiles, new Date(), input.warnings),
    coverage: coverageForGeneration(input.templateCoverage),
    warningCount: input.warnings.length,
  });
}

export function findSymbolEnvelope(input: {
  symbols: readonly FindSymbolHit[];
  pagination: Pagination;
  freshness: Freshness;
  coverage: Coverage;
}): FindSymbolEnvelope {
  return {
    contractVersion,
    command: "find_symbol",
    success: true,
    responseToolVersion: appVersion,
    warningCount: input.freshness.diagnostics.length,
    freshness: input.freshness,
    coverage: input.coverage,
    pagination: input.pagination,
    result: {
      symbols: input.symbols.map((symbol) => ({
        id: symbol.id,
        kind: symbol.kind,
        qualifiedName: symbol.qualifiedName,
        project: symbol.project,
        path: symbol.path,
        line: symbol.line,
        column: symbol.column,
        signatures: [...symbol.signatures],
      })),
    },
  };
}

export function findReferencesEnvelope(input: {
  references: readonly FindReferenceHit[];
  pagination: Pagination;
  freshness: Freshness;
  coverage: Coverage;
}): FindReferencesEnvelope {
  return {
    contractVersion,
    command: "find_references",
    success: true,
    responseToolVersion: appVersion,
    warningCount: input.freshness.diagnostics.length,
    freshness: input.freshness,
    coverage: input.coverage,
    pagination: input.pagination,
    result: {
      references: input.references.map((reference) => ({
        targetId: reference.targetId,
        kind: reference.kind,
        project: reference.project,
        path: reference.path,
        line: reference.line,
        column: reference.column,
      })),
    },
  };
}

export function commandErrorEnvelope(command: string | null, error: CommandError): ErrorEnvelope {
  return {
    contractVersion,
    command,
    success: false,
    responseToolVersion: appVersion,
    warningCount: error.warningCount,
    freshness: error.freshness,
    coverage: error.coverage,
    error: {
      code: error.code,
      message: error.message,
      diagnostics: [...error.diagnostics],
    },
  };
}

export function invalidArgumentsEnvelope(command: string | null): ErrorEnvelope {
  return {
    contractVersion,
    command,
    success: false,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: null,
    coverage: null,
    error: {
      code: "invalid_arguments",
      message: "Unknown command.",
      diagnostics: [],
    },
  };
}

export function formatEnvelope(
  envelope:
    | SuccessEnvelope
    | IndexSuccessEnvelope
    | FindSymbolEnvelope
    | FindReferencesEnvelope
    | ErrorEnvelope
    | ConfigureAiSuccessEnvelope,
): string {
  return `${JSON.stringify(envelope, null, 2)}\n`;
}
