using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas;

public sealed partial class SolutionAnalysis
{
    /// <summary>
    /// Replaces the text of every open document for one source file. The file on disk is left unchanged.
    /// The next query uses this text. Returns the workspace-relative paths updated. An empty list means
    /// the path is not an open source document.
    /// </summary>
    public string[] ApplyDocumentText(string path, string text)
    {
        var ids = DocumentsAt(path).Select(document => document.Id).ToArray();
        if (ids.Length == 0)
            return [];
        var updated = solution;
        foreach (var id in ids)
        {
            var document = updated.GetDocument(id) ?? throw new AnalysisException($"Open document disappeared: {path}");
            updated = document.WithText(SourceText.From(text)).Project.Solution;
        }
        if (!workspace.TryApplyChanges(updated))
            throw new AnalysisException($"The workspace rejected the text change: {path}");
        solution = workspace.CurrentSolution;
        return ids.Select(id => Relative(solution.GetDocument(id)!.FilePath!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private Document[] DocumentsAt(string path)
    {
        var full = SourcePaths.Real(Path.GetFullPath(path, directory));
        return solution.Projects.SelectMany(project => project.Documents)
            .Where(document => document.FilePath is not null && SnapshotInputModel.PathComparer.Equals(SourcePaths.Real(document.FilePath), full))
            .DistinctBy(document => document.Id)
            .ToArray();
    }
}
