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
    /// Applies one file's text in memory and replaces that file's rows in a private working copy.
    /// Method, reference, type, symbol, fact, search and duplicate rows come from the same builders as a full index.
    /// The stored file hash and the incremental tree hash are the applied text, written in that same transaction.
    /// A path that is not an open document throws <see cref="AnalysisException"/> and does not write.
    /// </summary>
    public async Task UpdateWorkingCopyAsync(string workingCopy, string path, string text, CancellationToken cancellationToken = default)
    {
        var updated = ApplyDocumentText(path, text);
        if (updated.Length == 0)
            throw new AnalysisException($"The path is not an open source document: {path}");
        var rows = await FileRowsAsync(path, cancellationToken);
        var (declared, _) = await SourceDeclarationsAsync(cancellationToken);
        var symbols = await GetDocumentedSymbolsAsync(cancellationToken);
        var catalog = await GetStructuralCatalogAsync(symbols, cancellationToken);
        var state = new SqliteIndexReader(workingCopy).Use(session => session.IncrementalState())
            ?? throw new AnalysisException("The working copy has no incremental state.");
        var duplicates = await DuplicateFinder.CreateAsync(solution, SolutionPath, state, cancellationToken);
        var (contentHash, surfaceHash) = await AppliedTreeHashAsync(path, cancellationToken);
        SqliteIndexWriter.UpdateWorkingCopy(workingCopy, Path.GetFullPath(path, directory), updated[0], text,
            rows.Methods, rows.References, symbols, catalog, duplicates, IndexedTypes(declared), contentHash, surfaceHash);
    }

    private IndexedType[] IndexedTypes(IEnumerable<ISymbol> symbols)
    {
        var types = new Dictionary<string, IndexedType>(StringComparer.Ordinal);
        foreach (var type in symbols.OfType<INamedTypeSymbol>())
        {
            var location = type.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            var document = location?.SourceTree is { } tree ? solution.GetDocument(tree) : null;
            if (location is null || document?.Project.FilePath is null)
                continue;
            var project = Relative(document.Project.FilePath);
            var id = project + "::" + type.GetDocumentationCommentId();
            types[id] = new IndexedType(id, type.ToDisplayString(), type.TypeKind.ToString().ToLowerInvariant(), project, Position(location));
        }
        return types.Values.OrderBy(type => type.Name, StringComparer.Ordinal).ThenBy(type => type.Id, StringComparer.Ordinal).ToArray();
    }

    private async Task<(string Content, string? Surface)> AppliedTreeHashAsync(string path, CancellationToken cancellationToken)
    {
        string? content = null;
        string? surface = null;
        foreach (var document in DocumentsAt(path))
        {
            var text = await document.GetTextAsync(cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new AnalysisException("The edited document has no syntax tree: " + path);
            var nextContent = IncrementalDigest.Text(text.ToString());
            var nextSurface = BodySurface.TryHash(root, text);
            if (content is not null && (content != nextContent || surface != nextSurface))
                throw new AnalysisException("Open documents for one file do not share one text hash: " + path);
            content = nextContent;
            surface = nextSurface;
        }
        return (content!, surface);
    }

    private string OneRelativePath(Document[] documents, string path)
    {
        var relative = documents.Select(document => Relative(document.FilePath!)).Distinct(StringComparer.Ordinal).ToArray();
        if (relative.Length != 1)
            throw new AnalysisException($"Open documents do not share one workspace-relative path: {path}");
        return relative[0];
    }
}
