using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas;

// An internal qualification seam, reachable by the separate benchmark executable
// and tests. Virtual results cannot pass IndexStore's normal load/publication gate.
internal sealed record BenchmarkOverlay(string PhysicalPath, string Before, string After);

public sealed partial class SolutionAnalysis
{
    internal static Task<CodebaseIndex> CreateBenchmarkIndexAsync(string path, CodebaseIndex? baseline,
        BenchmarkOverlay? overlay, bool incremental, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateIndexCoreAsync(path, cancellationToken, progress, 2, baseline, incremental, overlay);

    private async Task<SolutionAnalysis> ApplyBenchmarkOverlayAsync(BenchmarkOverlay overlay, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(overlay.PhysicalPath);
        var edited = solution;
        var matched = 0;
        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            if (document.FilePath is not { } file || !SnapshotInputModel.PathComparer.Equals(Path.GetFullPath(file), path))
                continue;
            var text = await document.GetTextAsync(cancellationToken);
            var source = text.ToString();
            var start = source.IndexOf(overlay.Before, StringComparison.Ordinal);
            if (start < 0 || source.IndexOf(overlay.Before, start + overlay.Before.Length, StringComparison.Ordinal) >= 0)
                throw new AnalysisException("Benchmark edit must match exactly once in each linked context.");
            var replacement = source[..start] + overlay.After + source[(start + overlay.Before.Length)..];
            edited = edited.WithDocumentText(document.Id, SourceText.From(replacement, text.Encoding));
            matched++;
        }
        if (matched == 0) throw new AnalysisException("Benchmark overlay did not match a loaded source document.");
        foreach (var project in edited.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var errors = compilation?.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()).ToArray();
            if (errors is null || errors.Length != 0)
                throw new AnalysisException("Benchmark overlay compilation failed.", errors);
        }
        // The parent analysis retains ownership of the shared workspace.
        return new SolutionAnalysis(workspace, edited, SolutionPath, warnings) { ownsWorkspace = false };
    }
}
