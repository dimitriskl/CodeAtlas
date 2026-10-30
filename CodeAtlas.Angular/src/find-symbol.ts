import { coverageForGeneration, staticCoverage } from "./coverage.js";
import {
  findSymbolEnvelope,
  commandErrorEnvelope,
  staleIndexError,
  trackedFreshness,
  unknownFreshness,
  type ErrorEnvelope,
  type FindSymbolEnvelope,
  type FindSymbolHit,
  type Pagination,
} from "./envelope.js";
import { asCommandError, CommandError } from "./errors.js";
import { readPublishedSymbols, type StoredSymbol } from "./generation-store.js";

const maximumLimit = 1000;
const defaultLimit = 100;

export type FindSymbolArguments = {
  workspace: string;
  name: string;
  kind?: string;
  project?: string;
  offset?: number;
  limit?: number;
  snapshotId?: string;
};

export function executeFindSymbol(args: FindSymbolArguments): FindSymbolEnvelope | ErrorEnvelope {
  try {
    return queryFindSymbol(args);
  } catch (error) {
    return commandErrorEnvelope("find_symbol", asCommandError(error));
  }
}

function queryFindSymbol(args: FindSymbolArguments): FindSymbolEnvelope {
  if (args.name.length === 0) {
    throw new CommandError("invalid_arguments", "name must not be empty.", 2);
  }
  if (args.kind !== undefined) {
    if (args.kind.length === 0) {
      throw new CommandError("invalid_arguments", "kind must not be empty.", 2);
    }
    if (!staticCoverage().symbols.includes(args.kind)) {
      throw new CommandError("invalid_arguments", "Unknown symbol kind.", 2);
    }
  }
  if (args.project !== undefined && args.project.length === 0) {
    throw new CommandError("invalid_arguments", "project must not be empty.", 2);
  }
  if (args.snapshotId !== undefined && args.snapshotId.length === 0) {
    throw new CommandError("invalid_arguments", "snapshotId must not be empty.", 2);
  }
  const offset = requireOffset(args.offset);
  const limit = requireLimit(args.limit);
  if (offset > 0 && args.snapshotId === undefined) {
    throw new CommandError("invalid_arguments", "offset greater than zero requires snapshotId.", 2);
  }

  const published = readPublishedSymbols(args.workspace);
  if (
    args.snapshotId !== undefined &&
    (published === null || published.status.generationId !== args.snapshotId)
  ) {
    throw new CommandError(
      "snapshot_mismatch",
      "The index was replaced. Start a new query at offset zero.",
    );
  }
  if (published === null) {
    return findSymbolEnvelope({
      symbols: [],
      pagination: page(offset, limit, 0, 0, null),
      freshness: unknownFreshness(),
      coverage: staticCoverage(),
    });
  }
  if (published.status.changedFiles.length > 0) {
    throw staleIndexError({
      changedFiles: published.status.changedFiles,
      warnings: published.status.warnings,
      templateCoverage: published.status.templateCoverage,
    });
  }

  const matches = published.symbols
    .filter((symbol) => nameMatches(symbol, args.name))
    .filter((symbol) => args.kind === undefined || symbol.kind === args.kind)
    .filter((symbol) => args.project === undefined || symbol.project === args.project)
    .sort((left, right) => (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));
  const symbols = matches.slice(offset, offset + limit).map(toHit);
  return findSymbolEnvelope({
    symbols,
    pagination: page(offset, limit, symbols.length, matches.length, published.status.generationId),
    freshness: trackedFreshness(
      published.status.changedFiles,
      undefined,
      published.status.warnings,
    ),
    coverage: coverageForGeneration(published.status.templateCoverage),
  });
}

function requireOffset(value: number | undefined): number {
  const offset = value ?? 0;
  if (!Number.isInteger(offset) || offset < 0) {
    throw new CommandError("invalid_arguments", "offset must be a nonnegative integer.", 2);
  }
  return offset;
}

function requireLimit(value: number | undefined): number {
  const limit = value ?? defaultLimit;
  if (!Number.isInteger(limit) || limit < 1 || limit > maximumLimit) {
    throw new CommandError("invalid_arguments", "limit must be an integer from 1 through 1000.", 2);
  }
  return limit;
}

function nameMatches(symbol: StoredSymbol, name: string): boolean {
  return symbol.qualifiedName === name || simpleName(symbol.qualifiedName) === name;
}

function simpleName(qualifiedName: string): string {
  const parts = qualifiedName.split(".");
  for (let index = parts.length - 1; index >= 0; index -= 1) {
    const part = parts[index];
    if (part !== undefined && part !== "<static>") return part;
  }
  return qualifiedName;
}

function toHit(symbol: StoredSymbol): FindSymbolHit {
  return {
    id: symbol.id,
    kind: symbol.kind,
    qualifiedName: symbol.qualifiedName,
    project: symbol.project,
    path: symbol.path,
    line: symbol.line,
    column: symbol.column,
    signatures: [...symbol.signatures],
  };
}

function page(
  offset: number,
  limit: number,
  returned: number,
  total: number,
  snapshotId: string | null,
): Pagination {
  const hasMore = offset + returned < total;
  return {
    offset,
    limit,
    returned,
    total,
    hasMore,
    nextOffset: hasMore ? offset + returned : null,
    snapshotId,
    continuationSupported: snapshotId !== null,
  };
}
