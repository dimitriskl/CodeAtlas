using System.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeAtlas;

/// <summary>
/// A per-project compilation used for semantic questions during indexing.
/// Bound method bodies stay on the compilation that answered them. The workspace
/// compilation is shared by every later project, so those questions are asked on a
/// clone that shares the syntax trees and project references. Dropping the clone
/// lets one project's bound state be collected before the next project is bound.
/// </summary>
internal static class BindingScratch
{
    internal static async Task<CSharpCompilation> CloneAsync(Project project, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not CSharpCompilation compilation)
            throw new AnalysisException("Analysis requires a C# compilation.");
        return compilation.Clone();
    }

    /// <summary>
    /// Collects a clone once the heap is large enough that another project's bound
    /// state would stack on top of it. Small fixtures are left to the ordinary collector.
    /// Compaction is required for the runtime to return empty segments to the OS.
    /// Finalizers are not drained: a SQLite handle may still be open on this thread.
    /// </summary>
    internal static void Release()
    {
        if (GC.GetTotalMemory(forceFullCollection: false) < 256L * 1024 * 1024)
            return;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
