import { staticCoverage, type Coverage } from "./coverage.js";
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

export type ErrorEnvelope = {
  contractVersion: number;
  command: string | null;
  success: false;
  responseToolVersion: string;
  warningCount: number;
  freshness: null;
  coverage: null;
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

export function coverageEnvelope(now: Date = new Date()): SuccessEnvelope {
  return {
    contractVersion,
    command: "coverage",
    success: true,
    responseToolVersion: appVersion,
    warningCount: 0,
    freshness: unknownFreshness(now),
    coverage: staticCoverage(),
    pagination: {
      offset: 0,
      limit: 0,
      returned: 0,
      total: 0,
      hasMore: false,
      nextOffset: null,
      snapshotId: null,
      continuationSupported: false,
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

export function formatEnvelope(envelope: SuccessEnvelope | ErrorEnvelope): string {
  return `${JSON.stringify(envelope, null, 2)}\n`;
}
