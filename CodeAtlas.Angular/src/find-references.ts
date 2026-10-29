import { coverageForGeneration, staticCoverage } from "./coverage.js";
import {
  findReferencesEnvelope,
  commandErrorEnvelope,
  trackedFreshness,
  type ErrorEnvelope,
  type FindReferencesEnvelope,
} from "./envelope.js";
import { asCommandError, CommandError } from "./errors.js";
import { readPublishedReferences, type StoredReference } from "./generation-store.js";
import { page, refuseStaleIndex, requireCurrentSnapshot, requireQueryPage } from "./query-page.js";
import { compareCodeUnit } from "./workspace-storage.js";

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
  const { offset, limit } = requireQueryPage(args);

  const published = readPublishedReferences(args.workspace);
  requireCurrentSnapshot(args.snapshotId, published?.status.generationId ?? null);
  if (published === null) {
    throw new CommandError("symbol_not_found", symbolNotFound);
  }
  refuseStaleIndex(published.status);
  if (!published.symbols.some((symbol) => symbol.id === args.id)) {
    throw new CommandError("symbol_not_found", symbolNotFound);
  }

  const allowed = args.kinds === undefined ? undefined : new Set(args.kinds);
  const matches = published.references
    .filter((reference) => reference.targetId === args.id)
    .filter((reference) => args.project === undefined || reference.project === args.project)
    .filter((reference) => allowed === undefined || allowed.has(reference.kind))
    .sort(compareReferences);
  const references = matches.slice(offset, offset + limit);
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

function compareReferences(left: StoredReference, right: StoredReference): number {
  const pathOrder = compareCodeUnit(left.path, right.path);
  if (pathOrder !== 0) return pathOrder;
  if (left.line !== right.line) return left.line - right.line;
  if (left.column !== right.column) return left.column - right.column;
  return compareCodeUnit(left.kind, right.kind);
}
