import type { Coverage } from "./coverage.js";
import { staleIndexError, type Pagination } from "./envelope.js";
import { CommandError } from "./errors.js";

const maximumLimit = 1000;
const defaultLimit = 100;

export function requireQueryPage(args: { snapshotId?: string; offset?: number; limit?: number }): {
  offset: number;
  limit: number;
} {
  if (args.snapshotId !== undefined && args.snapshotId.length === 0) {
    throw new CommandError("invalid_arguments", "snapshotId must not be empty.", 2);
  }
  const offset = requireOffset(args.offset);
  const limit = requireLimit(args.limit);
  if (offset > 0 && args.snapshotId === undefined) {
    throw new CommandError("invalid_arguments", "offset greater than zero requires snapshotId.", 2);
  }
  return { offset, limit };
}

/** The requested snapshot must be the published generation. A missing generation is not that snapshot. */
export function requireCurrentSnapshot(
  snapshotId: string | undefined,
  generationId: string | null,
): void {
  if (snapshotId !== undefined && generationId !== snapshotId) {
    throw new CommandError(
      "snapshot_mismatch",
      "The index was replaced. Start a new query at offset zero.",
    );
  }
}

export function refuseStaleIndex(status: {
  changedFiles: readonly string[];
  warnings: readonly string[];
  templateCoverage: Coverage["templateCoverage"];
}): void {
  if (status.changedFiles.length > 0) {
    throw staleIndexError({
      changedFiles: status.changedFiles,
      warnings: status.warnings,
      templateCoverage: status.templateCoverage,
    });
  }
}

export function page(
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
