using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeAtlas;

internal sealed record RefreshedReferences(Dictionary<string, SourceReference[]> References, RefreshSummary Summary);

public sealed partial class SolutionAnalysis
{
    internal async Task<RefreshedReferences> CollectRefreshReferencesAsync(CodebaseIndex? previous,
        CapturedIncrementalState current, SnapshotInputManifest inputs, MethodInfo[] methods,
        bool incremental, int workers, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var decision = incremental
            ? IncrementalGate.Decide(previous, previous?.IncrementalState, current.State, inputs, SolutionPath)
            : RefreshDecision.Full("explicit_full_index");
        // Defensive declaration-identity check in addition to exact source surfaces.
        if (decision.CanReuse && previous is not null
            && !previous.Methods.Select(method => (method.Id, method.Name, method.Signature, method.Project, method.ContainingType))
                .SequenceEqual(methods.Select(method => (method.Id, method.Name, method.Signature, method.Project, method.ContainingType))))
            decision = RefreshDecision.Full("declared_method_set_changed");

        var allTrees = current.State.Projects.SelectMany(project => project.Trees).ToArray();
        var physicalChanges = decision.ChangedTrees.Select(tree => tree.PhysicalPath).OfType<string>()
            .ToHashSet(SnapshotInputModel.PathComparer);
        var keys = decision.ChangedTrees.Select(tree => tree.Key).ToHashSet(StringComparer.Ordinal);
        var origins = decision.ChangedTrees.Select(tree => (tree.Project, tree.OriginPath)).ToHashSet();
        // Expand every linked context and any duplicate origin that cannot be
        // distinguished in persisted SourceReference. Recompute the whole origin.
        var dirtyTrees = allTrees.Where(tree => keys.Contains(tree.Key)
            || (tree.PhysicalPath is not null && physicalChanges.Contains(tree.PhysicalPath))
            || origins.Contains((tree.Project, tree.OriginPath))).ToArray();
        if (decision.CanReuse && dirtyTrees.Any(tree => !tree.Searchable || !current.SearchDocuments.ContainsKey(tree.Key)))
            decision = RefreshDecision.Full("unsearchable_linked_context");
        var documents = dirtyTrees.Where(tree => current.SearchDocuments.ContainsKey(tree.Key))
            .Select(tree => current.SearchDocuments[tree.Key]).ToImmutableHashSet();
        var dirtyOrigins = dirtyTrees.Select(tree => (tree.Project, tree.OriginPath)).ToHashSet();

        var results = new SourceReference[methods.Length][];
        long reused = 0;
        var completed = 0;
        var gate = new object();
        progress?.Report(new(IndexStage.FindingReferences, 0, methods.Length,
            decision.CanReuse ? $"Incremental: {documents.Count} changed document contexts" : decision.FallbackReason));
        await Parallel.ForEachAsync(Enumerable.Range(0, methods.Length),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (position, token) =>
            {
                var method = methods[position];
                if (!decision.CanReuse)
                    results[position] = (await FindReferencesCoreAsync(method.Id, null, token)).ToArray();
                else
                {
                    // Do not restrict selected targets to old references or edited declarations:
                    // a body can start referencing a previously unused method/overload.
                    var oldOrigins = previous!.References[method.Id]
                        .Where(reference => !dirtyOrigins.Contains((reference.Project, reference.Location.File))).ToArray();
                    var refreshedOrigins = documents.Count == 0 ? []
                        : (await FindReferencesCoreAsync(method.Id, documents, token)).ToArray();
                    results[position] = CanonicalReferences(oldOrigins.Concat(refreshedOrigins));
                    Interlocked.Add(ref reused, oldOrigins.LongLength);
                }
                lock (gate) progress?.Report(new(IndexStage.FindingReferences, ++completed, methods.Length));
            });
        var references = methods.Select((method, position) => (method.Id, References: results[position]))
            .ToDictionary(item => item.Id, item => item.References, StringComparer.Ordinal);
        var mode = !decision.CanReuse ? "full" : documents.Count == 0 ? "incremental-noop" : "incremental-body";
        return new(references, new(mode, decision.CanReuse ? null : decision.FallbackReason, previous?.IndexId,
            decision.CanReuse ? documents.Count : 0, allTrees.Length,
            decision.CanReuse && documents.Count == 0 ? 0 : methods.Length, reused));
    }
}
