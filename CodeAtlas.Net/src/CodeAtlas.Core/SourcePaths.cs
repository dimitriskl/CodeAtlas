using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace CodeAtlas;

/// <summary>Real paths for included source. The stored form stays a solution-relative path.</summary>
internal static class SourcePaths
{
    private static readonly ConcurrentDictionary<string, string> Resolved = new(SnapshotInputModel.PathComparer);

    public static string Relative(string solutionDirectory, string path) =>
        Path.GetRelativePath(Real(solutionDirectory), Real(path)).Replace('\\', '/');

    public static string Real(string path)
    {
        path = Path.GetFullPath(path);
        return Resolved.GetOrAdd(path, Resolve);
    }

    public static Solution Collapse(Solution solution, string solutionDirectory, ICollection<string> warnings)
    {
        foreach (var projectId in solution.ProjectIds.ToArray())
            solution = CollapseProject(solution, projectId, solutionDirectory, warnings);
        return solution;
    }

    public static Solution Adopt(Workspace workspace, Solution solution)
    {
        var method = typeof(Workspace).GetMethod("SetCurrentSolution", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Solution)])
            ?? throw new AnalysisException("The loaded solution could not be updated to real source paths.");
        try
        {
            return method.Invoke(workspace, [solution]) as Solution
                ?? throw new AnalysisException("The loaded solution could not be updated to real source paths.");
        }
        catch (TargetInvocationException exception)
        {
            throw new AnalysisException("The loaded solution could not be updated to real source paths.",
                [exception.InnerException?.Message ?? exception.Message]);
        }
    }

    private static Solution CollapseProject(Solution solution, ProjectId projectId, string solutionDirectory, ICollection<string> warnings)
    {
        var project = solution.GetProject(projectId)!;
        var drop = new List<OpenFile>();
        var keep = new Dictionary<string, OpenFile>(SnapshotInputModel.PathComparer);
        var repath = new List<OpenFile>();
        foreach (var file in OpenFiles(project))
        {
            var full = Path.GetFullPath(file.Path);
            var real = Real(full);
            var linked = !SnapshotInputModel.PathComparer.Equals(full, real);
            if (linked && !IsInside(solutionDirectory, real))
            {
                drop.Add(file);
                warnings.Add("Skipped symlink to a file outside the solution: " + Display(solutionDirectory, full));
                continue;
            }
            if (!keep.TryGetValue(real, out var existing))
            {
                keep[real] = file;
                if (linked) repath.Add(file with { Path = real });
                continue;
            }
            var winner = Prefer(existing, file, real);
            drop.Add(winner.Id == existing.Id ? file : existing);
            keep[real] = winner;
            if (winner.Id == file.Id && linked) repath.Add(file with { Path = real });
        }
        if (drop.Count == 0 && repath.Count == 0) return solution;
        foreach (var file in drop)
            solution = Remove(solution, file);
        foreach (var file in repath)
            if (file.Kind == FileKind.Source && solution.GetDocument(file.Id) is not null)
                solution = solution.WithDocumentFilePath(file.Id, file.Path);
        return solution;
    }

    private static OpenFile Prefer(OpenFile existing, OpenFile candidate, string real)
    {
        if (SnapshotInputModel.PathComparer.Equals(Path.GetFullPath(existing.Path), real)) return existing;
        if (SnapshotInputModel.PathComparer.Equals(Path.GetFullPath(candidate.Path), real)) return candidate;
        return existing;
    }

    public static string? Tracked(string root, string path)
    {
        var real = Real(path);
        if (!SnapshotInputModel.PathComparer.Equals(path, real) && !IsInside(root, real)) return null;
        return SnapshotInputModel.PathComparer.Equals(path, real) ? path : real;
    }

    public static bool IsInside(string solutionDirectory, string realPath)
    {
        var relative = Path.GetRelativePath(Real(solutionDirectory), realPath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Display(string solutionDirectory, string full) =>
        Path.GetRelativePath(Path.GetFullPath(solutionDirectory), full).Replace('\\', '/');

    private static IEnumerable<OpenFile> OpenFiles(Project project)
    {
        foreach (var document in project.Documents)
            if (document.FilePath is { } source) yield return new(document.Id, source, FileKind.Source);
        foreach (var document in project.AdditionalDocuments)
            if (document.FilePath is { } additional) yield return new(document.Id, additional, FileKind.Additional);
        foreach (var document in project.AnalyzerConfigDocuments)
            if (document.FilePath is { } analyzer) yield return new(document.Id, analyzer, FileKind.Analyzer);
    }

    private static Solution Remove(Solution solution, OpenFile file) => file.Kind switch
    {
        FileKind.Source => solution.GetDocument(file.Id) is null ? solution : solution.RemoveDocument(file.Id),
        FileKind.Additional => solution.GetAdditionalDocument(file.Id) is null ? solution : solution.RemoveAdditionalDocument(file.Id),
        _ => solution.GetAnalyzerConfigDocument(file.Id) is null ? solution : solution.RemoveAnalyzerConfigDocument(file.Id)
    };

    private static string Resolve(string start)
    {
        var path = start;
        var seen = new HashSet<string>(SnapshotInputModel.PathComparer);
        while (seen.Add(path))
        {
            var next = ResolveParents(path);
            if (SnapshotInputModel.PathComparer.Equals(next, path)) return path;
            if (Resolved.TryGetValue(next, out var known)) return known;
            path = next;
        }
        return path;
    }

    private static string ResolveParents(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var target = LinkTarget(current);
            if (target is null) continue;
            var rest = string.Join(Path.DirectorySeparatorChar, parts.Skip(i + 1));
            return Path.GetFullPath(rest.Length == 0 ? target : Path.Combine(target, rest));
        }
        return current;
    }

    private static string? LinkTarget(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private readonly record struct OpenFile(DocumentId Id, string Path, FileKind Kind);
    private enum FileKind { Source, Additional, Analyzer }
}
