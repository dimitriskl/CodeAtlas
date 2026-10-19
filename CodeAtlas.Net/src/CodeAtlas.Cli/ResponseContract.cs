using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas;

namespace CodeAtlas.Cli;

internal static class ResponseContract
{
    public const int Version = 1;

    public static object Coverage => new
    {
        language = "C#",
        symbols = new[] { "ordinary-source-methods", "explicit-interface-source-methods" },
        references = "static-source-references-with-related-definition-identity",
        runtimeCallGraph = false,
        exclusions = new[] { "constructors", "property-accessors", "local-functions", "metadata-only-methods",
            "reflection", "dependency-injection-resolution", "complete-virtual-dispatch" },
        limitations = new[] { "source-generator-inputs-not-fully-covered", "multi-targeting-not-fully-covered" }
    };

    public static object Freshness(IndexFreshness freshness) => new
    {
        state = freshness.Status,
        freshness.CheckedAtUtc,
        inputCoverage = new
        {
            freshness.Coverage.Status, freshness.Coverage.Scope,
            issueCount = freshness.Coverage.Issues.Length,
            reasonCodes = freshness.Coverage.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        },
        freshness.Diagnostics,
        freshness.IsStale,
        freshness.ChangedFiles,
        scope = "tracked-input-content-hashes",
        coverageDetails = "See warningsFile for per-project input coverage details."
    };

    public static object LiveFreshness => new
    {
        state = "live-analysis",
        isStale = (bool?)null,
        changedFiles = Array.Empty<string>(),
        scope = "loaded-semantic-workspace",
        limitations = new[] { "not-a-persistent-snapshot", "inputs-may-change-after-workspace-loading" }
    };

    public static void AddEnvelope(JsonObject output, string? command, bool success, JsonSerializerOptions options,
        object? freshness = null, bool coverageAvailable = false)
    {
        output["contractVersion"] = Version;
        output["command"] = command;
        output["success"] = success;
        output["responseToolVersion"] = CodeAtlasVersion.Current;
        output["warningCount"] ??= 0;
        output["freshness"] = freshness is null ? null : JsonSerializer.SerializeToNode(freshness, options);
        output["coverage"] = coverageAvailable ? JsonSerializer.SerializeToNode(Coverage, options) : null;
    }

    public static (string Code, int Exit) Classify(Exception exception) => exception switch
    {
        OperationCanceledException => ("cancelled", 130),
        StaleIndexException => ("stale_index", 3),
        QueryException { Code: "snapshot_mismatch" } => ("snapshot_mismatch", 3),
        QueryException { Code: "unsupported_contract" } => ("unsupported_contract", 2),
        QueryException query => (query.Code, 1),
        IndexFormatException format => (format.Code, 1),
        ArgumentException => ("invalid_arguments", 2),
        _ => ("analysis_failed", 1)
    };
}
