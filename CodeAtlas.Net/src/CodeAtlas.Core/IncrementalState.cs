using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas;

public sealed record IncrementalTreeState(string Key, string Project, string OriginPath,
    string? PhysicalPath, bool Searchable, string ContentHash, string? SurfaceHash, string ParseOptionsHash);

public sealed record IncrementalProjectState(string Project, string ContextHash, IncrementalTreeState[] Trees);

public sealed record IncrementalSnapshotState(int Version, string IndexId, string SemanticToolHash, string InputManifestHash,
    IncrementalProjectState[] Projects, string[] UnsupportedReasons);

public sealed record RefreshSummary(string Mode, string? FallbackReason, string? BaselineIndexId,
    int ChangedDocumentContexts, int TotalDocumentContexts, int ReferenceSearches, long ReusedReferences);

internal sealed record RefreshDecision(bool CanReuse, string? FallbackReason, IncrementalTreeState[] ChangedTrees)
{
    internal static RefreshDecision Full(string reason) => new(false, reason, []);
}

internal static class IncrementalGate
{
    internal const int Version = 1;

    internal static RefreshDecision Decide(CodebaseIndex? previous, IncrementalSnapshotState? oldState,
        IncrementalSnapshotState current, SnapshotInputManifest currentInputs, string solutionPath)
    {
        var reason = BaselineFailure(previous, oldState, current, solutionPath);
        if (reason is not null) return RefreshDecision.Full(reason);
        var oldInputs = previous!.InputManifest!;
        reason = ContextFailure(previous, oldState!, current, oldInputs, currentInputs);
        if (reason is not null) return RefreshDecision.Full(reason);
        // Partial/unknown coverage alone does not veto reuse of a verified realized context.
        var changed = new List<IncrementalTreeState>();
        reason = ChangedTreesFailure(oldState!, current, changed);
        if (reason is not null) return RefreshDecision.Full(reason);
        reason = NonSourceFailure(oldInputs, current, currentInputs);
        return reason is not null ? RefreshDecision.Full(reason) : new(true, null, changed.ToArray());
    }

    private static string? BaselineFailure(CodebaseIndex? previous, IncrementalSnapshotState? oldState,
        IncrementalSnapshotState current, string solutionPath)
    {
        // .12 cannot seed old generated-tree/surface evidence retroactively.
        if (previous is null || oldState is null) return "baseline_metadata_missing";
        if (oldState.Version != Version || oldState.IndexId != previous.IndexId) return "baseline_metadata_incompatible";
        if (!WellFormed(oldState) || !WellFormed(current)) return "incremental_metadata_invalid";
        if (!SnapshotInputModel.PathComparer.Equals(previous.Solution, solutionPath)) return "solution_identity_changed";
        return previous.InputManifest is null ? "baseline_input_model_missing" : null;
    }

    private static string? ContextFailure(CodebaseIndex previous, IncrementalSnapshotState oldState,
        IncrementalSnapshotState current, SnapshotInputManifest oldInputs, SnapshotInputManifest currentInputs)
    {
        var reason = MetadataContextFailure(previous, oldState, current, oldInputs);
        if (reason is not null) return reason;
        var knownOldOrigins = oldState.Projects.SelectMany(project => project.Trees)
            .Select(tree => (tree.Project, tree.OriginPath)).ToHashSet();
        if (previous.References.Values.SelectMany(references => references)
            .Any(reference => !knownOldOrigins.Contains((reference.Project, reference.Location.File))))
            return "reference_origin_unverified";
        if (oldInputs.InputModelVersion != currentInputs.InputModelVersion || oldInputs.EnvironmentDigest != currentInputs.EnvironmentDigest)
            return "input_context_changed";
        return null;
    }

    private static string? MetadataContextFailure(CodebaseIndex previous, IncrementalSnapshotState oldState,
        IncrementalSnapshotState current, SnapshotInputManifest oldInputs)
    {
        if (oldState.InputManifestHash != IncrementalDigest.Json(oldInputs)
            || !oldState.Projects.Select(project => project.Project).Order(StringComparer.Ordinal)
                .SequenceEqual(previous.Projects.Select(project => project.Path).Order(StringComparer.Ordinal)))
            return "baseline_metadata_incompatible";
        if (oldState.SemanticToolHash != current.SemanticToolHash) return "semantic_tool_changed";
        if (oldState.UnsupportedReasons.Length != 0 || current.UnsupportedReasons.Length != 0) return "semantic_context_unverified";
        return null;
    }

    private static string? ChangedTreesFailure(IncrementalSnapshotState oldState, IncrementalSnapshotState current,
        List<IncrementalTreeState> changed)
    {
        if (oldState.Projects.Length != current.Projects.Length) return "project_inventory_changed";
        for (var projectIndex = 0; projectIndex < current.Projects.Length; projectIndex++)
        {
            var oldProject = oldState.Projects[projectIndex];
            var newProject = current.Projects[projectIndex];
            var reason = ProjectFailure(oldProject, newProject);
            if (reason is not null) return reason;
            // Compilation tree order participates in declaration location selection.
            for (var treeIndex = 0; treeIndex < newProject.Trees.Length; treeIndex++)
            {
                var before = oldProject.Trees[treeIndex];
                var after = newProject.Trees[treeIndex];
                reason = TreeInventoryFailure(before, after);
                if (reason is not null) return reason;
                if (before.ContentHash == after.ContentHash) continue;
                reason = ChangedTreeFailure(before, after);
                if (reason is not null) return reason;
                changed.Add(after);
            }
        }
        return null;
    }

    private static string? ProjectFailure(IncrementalProjectState before, IncrementalProjectState after)
    {
        if (before.Project != after.Project || before.ContextHash != after.ContextHash) return "compilation_context_changed";
        return before.Trees.Length != after.Trees.Length ? "document_inventory_changed" : null;
    }

    private static string? TreeInventoryFailure(IncrementalTreeState before, IncrementalTreeState after)
    {
        if (before.Key != after.Key || before.Project != after.Project || before.OriginPath != after.OriginPath
            || before.PhysicalPath != after.PhysicalPath || before.Searchable != after.Searchable) return "document_inventory_changed";
        return before.ParseOptionsHash != after.ParseOptionsHash ? "parse_options_changed" : null;
    }

    private static string? ChangedTreeFailure(IncrementalTreeState before, IncrementalTreeState after)
    {
        if (!before.Searchable || !after.Searchable) return "unsearchable_changed_tree";
        return before.SurfaceHash is null || after.SurfaceHash is null || before.SurfaceHash != after.SurfaceHash
            ? "declaration_surface_changed" : null;
    }

    private static string? NonSourceFailure(SnapshotInputManifest oldInputs, IncrementalSnapshotState current,
        SnapshotInputManifest currentInputs)
    {
        // Unobserved project/config/DLL changes always require full-reference recomputation.
        var physicalSources = current.Projects.SelectMany(project => project.Trees)
            .Select(tree => tree.PhysicalPath).OfType<string>().ToHashSet(SnapshotInputModel.PathComparer);
        var oldFiles = new Dictionary<string, FileFingerprint>(oldInputs.Files, SnapshotInputModel.PathComparer);
        var newFiles = new Dictionary<string, FileFingerprint>(currentInputs.Files, SnapshotInputModel.PathComparer);
        foreach (var file in oldFiles.Keys.Union(newFiles.Keys, SnapshotInputModel.PathComparer))
            if ((!oldFiles.TryGetValue(file, out var before) || !newFiles.TryGetValue(file, out var after) || before != after)
                && !physicalSources.Contains(file)) return "non_source_input_changed";
        return DiscoveryChanged(oldInputs, currentInputs) ? "input_discovery_changed" : null;
    }

    private static bool DiscoveryChanged(SnapshotInputManifest before, SnapshotInputManifest after) =>
        IncrementalDigest.Json(before.DiscoveryScopes) != IncrementalDigest.Json(after.DiscoveryScopes)
        || IncrementalDigest.Json(before.DiscoveryRootStates.OrderBy(pair => pair.Key, SnapshotInputModel.PathComparer))
            != IncrementalDigest.Json(after.DiscoveryRootStates.OrderBy(pair => pair.Key, SnapshotInputModel.PathComparer))
        || IncrementalDigest.Json(before.DirectoryInventories) != IncrementalDigest.Json(after.DirectoryInventories);

    private static bool WellFormed(IncrementalSnapshotState state)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        return !string.IsNullOrWhiteSpace(state.IndexId) && Hash(state.SemanticToolHash) && Hash(state.InputManifestHash)
            && state.UnsupportedReasons is not null && state.UnsupportedReasons.All(reason => reason is not null)
            && state.Projects is not null && state.Projects.All(project => project is not null
                && !string.IsNullOrWhiteSpace(project.Project) && Hash(project.ContextHash)
                && project.Trees is not null && project.Trees.All(tree => tree is not null
                    && !string.IsNullOrWhiteSpace(tree.Key) && tree.Project == project.Project
                    && !string.IsNullOrWhiteSpace(tree.OriginPath) && Hash(tree.ContentHash)
                    && (tree.SurfaceHash is null || Hash(tree.SurfaceHash))
                    && (Hash(tree.ParseOptionsHash) || tree.ParseOptionsHash == "unsupported")
                    && (tree.PhysicalPath is null || Path.IsPathFullyQualified(tree.PhysicalPath))))
            && state.Projects.SelectMany(project => project.Trees).Select(tree => tree.Key)
                .Distinct(StringComparer.Ordinal).Count() == state.Projects.Sum(project => project.Trees.Length)
            && state.Projects.Select(project => project.Project).Distinct(StringComparer.Ordinal).Count() == state.Projects.Length;
    }
}

internal static class IncrementalDigest
{
    internal static string Json<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static string Text(string value)
    {
        // Exact source UTF-16 code units; invalid surrogate replacement cannot collide.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Length);
        foreach (var character in value) writer.Write((ushort)character);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
