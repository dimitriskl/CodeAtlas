import { coverageForGeneration, staticCoverage } from "./coverage.js";
import {
  findReferencesEnvelope,
  commandErrorEnvelope,
  staleIndexError,
  trackedFreshness,
  type ErrorEnvelope,
  type FindReferenceHit,
  type FindReferencesEnvelope,
  type Pagination,
} from "./envelope.js";
import { asCommandError, CommandError } from "./errors.js";
import { readPublishedReferences, type StoredReference } from "./generation-store.js";
import { compareCodeUnit } from "./workspace-storage.js";

const maximumLimit = 1000;
const defaultLimit = 100;
const referenceKinds = new Set(staticCoverage().referenceSymbolKinds);
const symbolNotFound = "Symbol id was not found.";

export type FindReferencesArguments = {
  workspace: string;
  id: string;
  kinds?: readonly string[];
  project?: string;
  offset?: number;
  limit?: number;
  snapshotId?: string;
};

export function executeFindReferences(
  args: FindReferencesArguments,
): FindReferencesEnvelope | ErrorEnvelope {
  try {
    return queryFindReferences(args);
  } catch (error) {
    return commandErrorEnvelope("find_references", asCommandError(error));
  }
}

function queryFindReferences(args: FindReferencesArguments): FindReferencesEnvelope {
  if (args.id.length === 0) {
    throw new CommandError("invalid_arguments", "id must not be empty.", 2);
  }
  if (args.kinds !== undefined) {
    if (args.kinds.length === 0) {
      throw new CommandError("invalid_arguments", "kinds must not be empty.", 2);
    }
    for (const kind of args.kinds) {
      if (!referenceKinds.has(kind)) {
        throw new CommandError("invalid_arguments", "Unknown reference kind.", 2);
      }
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

  const published = readPublishedReferences(args.workspace);
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
    throw new CommandError("symbol_not_found", symbolNotFound);
  }
  if (published.status.changedFiles.length > 0) {
    throw staleIndexError({
      changedFiles: published.status.changedFiles,
      warnings: published.status.warnings,
      templateCoverage: published.status.templateCoverage,
    });
  }
  if (!published.symbols.some((symbol) => symbol.id === args.id)) {
    throw new CommandError("symbol_not_found", symbolNotFound);
  }

  const allowed = args.kinds === undefined ? undefined : new Set(args.kinds);
  const matches = published.references
    .filter((reference) => reference.targetId === args.id)
    .filter((reference) => args.project === undefined || reference.project === args.project)
    .filter((reference) => allowed === undefined || allowed.has(reference.kind))
    .sort(compareReferences);
  const references = matches.slice(offset, offset + limit).map(toHit);
  return findReferencesEnvelope({
    references,
    pagination: page(
      offset,
      limit,
      references.length,
      matches.length,
      published.status.generationId,
    ),
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

function compareReferences(left: StoredReference, right: StoredReference): number {
  const pathOrder = compareCodeUnit(left.path, right.path);
  if (pathOrder !== 0) return pathOrder;
  if (left.line !== right.line) return left.line - right.line;
  if (left.column !== right.column) return left.column - right.column;
  return compareCodeUnit(left.kind, right.kind);
}

function toHit(reference: StoredReference): FindReferenceHit {
  return {
    targetId: reference.targetId,
    kind: reference.kind,
    project: reference.project,
    path: reference.path,
    line: reference.line,
    column: reference.column,
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
