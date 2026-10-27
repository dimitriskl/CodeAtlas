/** Declared analysis boundary. Not the symbols found in a generation. */
export type Coverage = {
  language: string;
  symbols: readonly string[];
  references: string;
  referenceSymbolKinds: readonly string[];
  templateCoverage: "unknown" | "partial" | "complete";
  runtimeCallGraph: boolean;
  exclusions: readonly string[];
  limitations: readonly string[];
};

/**
 * Edition boundary returned when no index is loaded.
 * Symbol and reference kinds are the product model from PRD 0001.
 * `templateCoverage` stays `unknown` until a workspace is read.
 */
export function staticCoverage(): Coverage {
  return {
    language: "TypeScript",
    symbols: [
      "component",
      "directive",
      "pipe",
      "service",
      "guard",
      "interceptor",
      "route",
      "class",
      "interface",
      "function",
      "method",
      "property",
      "input",
      "output",
      "type-alias",
      "enum",
    ],
    references: "static-source-and-template-references",
    referenceSymbolKinds: [
      "invocation",
      "template-call",
      "template-usage",
      "injection",
      "route",
      "reference",
    ],
    templateCoverage: "unknown",
    runtimeCallGraph: false,
    exclusions: ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    limitations: ["no-index"],
  };
}
