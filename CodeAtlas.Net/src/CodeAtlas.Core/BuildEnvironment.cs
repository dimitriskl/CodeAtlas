using System.Text.RegularExpressions;
using Microsoft.Build.Evaluation;

namespace CodeAtlas;

/// <summary>
/// Environment variables that can change the evaluated build without changing a file.
/// Only names referenced by the solution's own project files and imports are tracked.
/// SDK, NuGet and bin/obj files are skipped: their bytes are already hashed, and they
/// mention shell variables such as PATH that differ between terminals.
/// </summary>
internal static class BuildEnvironment
{
    private static readonly Regex PropertyReference = new(@"\$\(\s*([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentCall = new(@"GetEnvironmentVariable\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string Canonical(string name) =>
        OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;

    internal static IEnumerable<string> ReferencesIn(string text)
    {
        foreach (Match match in PropertyReference.Matches(text))
            yield return match.Groups[1].Value;
        foreach (Match match in EnvironmentCall.Matches(text))
            yield return match.Groups[1].Value;
    }

    /// <summary>
    /// Keeps a referenced name when it is an environment variable, or when the project
    /// references it but does not define it (so setting it later changes the build).
    /// </summary>
    internal static string[] Select(IEnumerable<string> referenced, IEnumerable<string> environmentNames,
        IEnumerable<string> definedNonEnvironment)
    {
        var environment = new HashSet<string>(environmentNames, SnapshotInputModel.PathComparer);
        var defined = new HashSet<string>(definedNonEnvironment, SnapshotInputModel.PathComparer);
        return referenced.Where(name => !string.IsNullOrWhiteSpace(name)).Select(Canonical)
            .Where(name => environment.Contains(name) || !defined.Contains(name))
            .Distinct(SnapshotInputModel.PathComparer).Order(SnapshotInputModel.PathComparer).ToArray();
    }

    internal static bool IsOutputPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var basePath = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(basePath, comparison)) return true;
        if (!basePath.EndsWith(Path.DirectorySeparatorChar)) basePath += Path.DirectorySeparatorChar;
        return full.StartsWith(basePath, comparison);
    }

    internal static bool IsSdkOrPackage(string path, string? dotnetRoot)
    {
        var full = Path.GetFullPath(path);
        if (dotnetRoot is not null && IsUnder(full, dotnetRoot)) return true;
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(nuget) && IsUnder(full, nuget)) return true;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && IsUnder(full, Path.Combine(profile, ".nuget"));
    }

    internal static string[] FromProject(Project project, string? dotnetRoot)
    {
        var environment = new HashSet<string>(SnapshotInputModel.PathComparer);
        var defined = new HashSet<string>(SnapshotInputModel.PathComparer);
        foreach (var property in project.AllEvaluatedProperties)
        {
            if (property.IsEnvironmentProperty) environment.Add(property.Name);
            else defined.Add(property.Name);
        }
        var referenced = new List<string>();
        foreach (var text in ProjectTexts(project, dotnetRoot))
            referenced.AddRange(ReferencesIn(text));
        return Select(referenced, environment, defined);
    }

    private static IEnumerable<string> ProjectTexts(Project project, string? dotnetRoot)
    {
        yield return Read(project.FullPath);
        foreach (var import in project.Imports)
        {
            var path = import.ImportedProject?.FullPath;
            if (string.IsNullOrWhiteSpace(path) || SnapshotInputModel.PathComparer.Equals(path, project.FullPath))
                continue;
            if (IsSdkOrPackage(path, dotnetRoot) || IsOutputPath(path)) continue;
            yield return Read(path);
        }
    }

    private static string Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AnalysisException("Could not read a project file while collecting build-environment references: " + path, [exception.Message]);
        }
    }
}
