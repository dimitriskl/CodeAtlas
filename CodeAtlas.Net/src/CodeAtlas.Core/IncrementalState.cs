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
        // .12 cannot seed old generated-tree/surface evidence retroactively.
        if (previous is null || oldState is null)
            return RefreshDecision.Full("baseline_metadata_missing");
        if (oldState.Version != Version || oldState.IndexId != previous.IndexId)
            return RefreshDecision.Full("baseline_metadata_incompatible");
        if (!WellFormed(oldState) || !WellFormed(current))
            return RefreshDecision.Full("incremental_metadata_invalid");
        if (!SnapshotInputModel.PathComparer.Equals(previous.Solution, solutionPath))
            return RefreshDecision.Full("solution_identity_changed");
        if (previous.InputManifest is not { } oldInputs)
            return RefreshDecision.Full("baseline_input_model_missing");
        if (oldState.InputManifestHash != IncrementalDigest.Json(oldInputs)
            || !oldState.Projects.Select(project => project.Project).Order(StringComparer.Ordinal)
                .SequenceEqual(previous.Projects.Select(project => project.Path).Order(StringComparer.Ordinal)))
            return RefreshDecision.Full("baseline_metadata_incompatible");
        if (oldState.SemanticToolHash != current.SemanticToolHash)
            return RefreshDecision.Full("semantic_tool_changed");
        if (oldState.UnsupportedReasons.Length != 0 || current.UnsupportedReasons.Length != 0)
            return RefreshDecision.Full("semantic_context_unverified");
        var knownOldOrigins = oldState.Projects.SelectMany(project => project.Trees)
            .Select(tree => (tree.Project, tree.OriginPath)).ToHashSet();
        if (previous.References.Values.SelectMany(references => references)
            .Any(reference => !knownOldOrigins.Contains((reference.Project, reference.Location.File))))
            return RefreshDecision.Full("reference_origin_unverified");
        if (oldInputs.InputModelVersion != currentInputs.InputModelVersion
            || oldInputs.EnvironmentDigest != currentInputs.EnvironmentDigest)
            return RefreshDecision.Full("input_context_changed");

        // Partial coverage/unknown freshness alone DOES NOT veto reuse. A complete
        // CURRENT realized semantic context can establish body-reference invariance.
        // Preserve the CURRENT manifest/coverage unchanged in the resulting index.
        if (oldState.Projects.Length != current.Projects.Length)
            return RefreshDecision.Full("project_inventory_changed");
        var changed = new List<IncrementalTreeState>();
        for (var projectIndex = 0; projectIndex < current.Projects.Length; projectIndex++)
        {
            var oldProject = oldState.Projects[projectIndex];
            var newProject = current.Projects[projectIndex];
            if (oldProject.Project != newProject.Project || oldProject.ContextHash != newProject.ContextHash)
                return RefreshDecision.Full("compilation_context_changed");
            // Compilation tree order participates in declaration location selection.
            if (oldProject.Trees.Length != newProject.Trees.Length)
                return RefreshDecision.Full("document_inventory_changed");
            for (var treeIndex = 0; treeIndex < newProject.Trees.Length; treeIndex++)
            {
                var before = oldProject.Trees[treeIndex];
                var after = newProject.Trees[treeIndex];
                if (before.Key != after.Key || before.Project != after.Project || before.OriginPath != after.OriginPath
                    || before.PhysicalPath != after.PhysicalPath || before.Searchable != after.Searchable)
                    return RefreshDecision.Full("document_inventory_changed");
                if (before.ParseOptionsHash != after.ParseOptionsHash)
                    return RefreshDecision.Full("parse_options_changed");
                if (before.ContentHash == after.ContentHash)
                    continue;
                if (!before.Searchable || !after.Searchable)
                    return RefreshDecision.Full("unsearchable_changed_tree");
                if (before.SurfaceHash is null || after.SurfaceHash is null || before.SurfaceHash != after.SurfaceHash)
                    return RefreshDecision.Full("declaration_surface_changed");
                changed.Add(after);
            }
        }

        // Unobserved non-source input changes always get full-reference recomputation,
        // even if current declarations appear equal. This includes .csproj/config/DLLs.
        var physicalSources = current.Projects.SelectMany(project => project.Trees)
            .Select(tree => tree.PhysicalPath).OfType<string>().ToHashSet(SnapshotInputModel.PathComparer);
        var oldFiles = new Dictionary<string, FileFingerprint>(oldInputs.Files, SnapshotInputModel.PathComparer);
        var newFiles = new Dictionary<string, FileFingerprint>(currentInputs.Files, SnapshotInputModel.PathComparer);
        foreach (var file in oldFiles.Keys.Union(newFiles.Keys, SnapshotInputModel.PathComparer))
            if ((!oldFiles.TryGetValue(file, out var before) || !newFiles.TryGetValue(file, out var after) || before != after)
                && !physicalSources.Contains(file))
                return RefreshDecision.Full("non_source_input_changed");
        if (IncrementalDigest.Json(oldInputs.DiscoveryScopes) != IncrementalDigest.Json(currentInputs.DiscoveryScopes)
            || IncrementalDigest.Json(oldInputs.DiscoveryRootStates.OrderBy(pair => pair.Key, SnapshotInputModel.PathComparer))
                != IncrementalDigest.Json(currentInputs.DiscoveryRootStates.OrderBy(pair => pair.Key, SnapshotInputModel.PathComparer))
            || IncrementalDigest.Json(oldInputs.DirectoryInventories) != IncrementalDigest.Json(currentInputs.DirectoryInventories))
            return RefreshDecision.Full("input_discovery_changed");
        return new(true, null, changed.ToArray());
    }

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
