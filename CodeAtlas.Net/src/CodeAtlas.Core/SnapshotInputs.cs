using System.Collections;

namespace CodeAtlas;

/// <summary>Disk-only freshness checks; no workspace or MSBuild registration is performed.</summary>
public static class SnapshotInputs
{
    public static CoverageSummary LegacyCoverage { get; } = new("partial", "legacy-input-model",
        [new("legacy_input_model", "Rebuild to capture supported build inputs and explicit analysis coverage.")]);

    public static async Task<IndexFreshness> CheckFreshnessAsync(CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        if (index.InputManifest is null)
        {
            // Legacy fingerprints can prove staleness but cannot establish full input coverage.
            try
            {
                var current = await InputTracker.CaptureAsync(index.InputRoots, index.ExplicitInputs, cancellationToken);
                var changed = InputTracker.Changes(index.Fingerprints, current);
                return Describe(new(changed.Length == 0 ? "unknown" : "stale", changed.Length != 0,
                    DateTimeOffset.UtcNow, changed, LegacyCoverage, []), index.Solution);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(false, []) { Diagnostics = ["Could not verify tracked inputs: " + exception.Message] };
            }
        }
        return Describe(await SnapshotInputModel.CheckAsync(index.InputManifest, SnapshotEnvironment.Capture(), cancellationToken), index.Solution);
    }

    private static IndexFreshness Describe(InputFreshnessResult result, string solution) =>
        new(result.TrackedInputsChanged, result.ChangedInputs.Select(path => Path.IsPathFullyQualified(path)
            ? Path.GetRelativePath(Path.GetDirectoryName(solution)!, path).Replace('\\', '/') : path).ToArray())
        { Status = result.Status, CheckedAtUtc = result.CheckedAtUtc, Coverage = result.Coverage, Diagnostics = result.Diagnostics };
}

internal static class SnapshotEnvironment
{
    internal static readonly object Gate = new();
    public static Dictionary<string, string> Raw() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, SnapshotInputModel.PathComparer);

    public static Dictionary<string, string> Capture()
    {
        lock (Gate)
        {
            var environment = Raw();
            // Provider settings belong to derived descriptions, not semantic build inputs.
            // Configuring or rotating a credential must not invalidate a code map.
            environment.Remove("CODEATLAS_AI_API_KEY");
            environment.Remove("CODEATLAS_AI_MODEL");
            environment.Remove("CODEATLAS_AI_ENDPOINT");
            return environment;
        }
    }
}
