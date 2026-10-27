using System.Collections.Immutable;
using CodeAtlas.Storage;
using Microsoft.CodeAnalysis;

namespace CodeAtlas;

/// <summary>One file's method and reference rows, in the shape <c>ReplaceFileRows</c> takes.</summary>
public sealed record FileIndexRows(MethodInfo[] Methods, IReadOnlyDictionary<string, SourceReference[]> References);

public sealed partial class SolutionAnalysis
{
    /// <summary>
    /// Builds one open file's method and reference rows from this workspace, including text applied in memory.
    /// Method rows and reference rows come from the same builders as a full index. Nothing is written.
    /// </summary>
    public async Task<FileIndexRows> FileRowsAsync(string path, CancellationToken cancellationToken = default)
    {
        var documents = DocumentsAt(path);
        if (documents.Length == 0)
            throw new AnalysisException($"The path is not an open source document: {path}");
        var relativeFile = OneRelativePath(documents, path);
        var (_, methods) = await SourceDeclarationsAsync(cancellationToken);
        var scope = documents.ToImmutableHashSet();
        var inFile = new List<MethodInfo>();
        var references = new Dictionary<string, SourceReference[]>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var definedHere = string.Equals(method.Location.File, relativeFile, StringComparison.Ordinal);
            var mine = (await FindReferencesCoreAsync(method.Id, scope, cancellationToken))
                .Where(reference => string.Equals(reference.Location.File, relativeFile, StringComparison.Ordinal))
                .ToArray();
            if (definedHere)
                inFile.Add(method);
            if (definedHere || mine.Length > 0)
                references[method.Id] = mine;
        }
        return new(inFile.ToArray(), references);
    }

    /// <summary>
    /// Applies one file's text in memory, builds that file's rows, and replaces them in a private working copy.
    /// A path that is not an open document throws <see cref="AnalysisException"/> and does not write.
    /// The file hash, manifest, and generation header stay as copied.
    /// </summary>
    public async Task UpdateWorkingCopyAsync(string workingCopy, string path, string text, CancellationToken cancellationToken = default)
    {
        var updated = ApplyDocumentText(path, text);
        if (updated.Length == 0)
            throw new AnalysisException($"The path is not an open source document: {path}");
        var rows = await FileRowsAsync(path, cancellationToken);
        SqliteIndexWriter.ReplaceFileRows(workingCopy, updated[0], rows.Methods, rows.References);
    }

    private string OneRelativePath(Document[] documents, string path)
    {
        var relative = documents.Select(document => Relative(document.FilePath!)).Distinct(StringComparer.Ordinal).ToArray();
        if (relative.Length != 1)
            throw new AnalysisException($"Open documents do not share one workspace-relative path: {path}");
        return relative[0];
    }
}
