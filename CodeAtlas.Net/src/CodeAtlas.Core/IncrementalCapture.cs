using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

internal sealed record CapturedIncrementalState(IncrementalSnapshotState State,
    IReadOnlyDictionary<string, Document> SearchDocuments);

public sealed partial class SolutionAnalysis
{
    internal async Task<CapturedIncrementalState> CaptureIncrementalStateAsync(string indexId,
        SnapshotInputManifest inputs, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        var states = new List<IncrementalProjectState>();
        var searchable = new Dictionary<string, Document>(StringComparer.Ordinal);
        var projects = solution.Projects.ToArray();
        if (projects.Select(project => project.FilePath).Distinct(SnapshotInputModel.PathComparer).Count() != projects.Length)
            reasons.Add("Multiple loaded target contexts share the current project-path symbol identity.");
        var compilations = new Dictionary<ProjectId, Compilation>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) throw new AnalysisException($"Could not compile project: {project.Name}.");
            compilations.Add(project.Id, compilation);
        }

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = Relative(project.FilePath!);
            var compilation = compilations[project.Id];
            var documents = project.Documents.Concat<Document>(await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToArray();
            var documentTrees = new Dictionary<SyntaxTree, Document>();
            foreach (var document in documents)
                if (await document.GetSyntaxTreeAsync(cancellationToken) is { } tree)
                    documentTrees[tree] = document;
            var treeStates = new List<IncrementalTreeState>();
            var ordinal = 0;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var text = await tree.GetTextAsync(cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);
                documentTrees.TryGetValue(tree, out var document);
                // Exact path and ordinal conservatively reject reorder/case/path changes.
                var key = JsonSerializer.Serialize(new[] { projectPath, project.Name, tree.FilePath, (ordinal++).ToString(CultureInfo.InvariantCulture) });
                var physical = document?.FilePath is { } file && Path.IsPathFullyQualified(file)
                    ? Path.GetFullPath(file) : null;
                // GetLineSpan(), used by the full index, uses the tree's original path.
                // Use exactly the same Relative helper so origin removal matches it.
                var originPath = string.IsNullOrEmpty(tree.FilePath) ? "" : Relative(tree.FilePath);
                if (originPath.Length == 0) reasons.Add(projectPath + ": tree has no origin path");
                var parseHash = "unsupported";
                if (tree.Options is CSharpParseOptions { Kind: SourceCodeKind.Regular } options)
                    parseHash = CaptureOptionProperties(options, reasons, projectPath);
                else reasons.Add(projectPath + ": non-regular/non-CSharp parse context");
                treeStates.Add(new(key, projectPath, originPath, physical, document is not null,
                    IncrementalDigest.Text(text.ToString()), BodySurface.TryHash(root, text), parseHash));
                if (document is not null) searchable.Add(key, document);
            }

            var references = new List<object>();
            foreach (var reference in compilation.References)
            {
                var bound = compilation.GetAssemblyOrModuleSymbol(reference);
                var boundIdentity = bound is IAssemblySymbol assembly ? assembly.Identity.GetDisplayName()
                    : bound is IModuleSymbol module ? module.ToDisplayString() : "<unresolved>";
                var properties = new { reference.Properties.Kind, reference.Properties.EmbedInteropTypes,
                    Aliases = reference.Properties.Aliases.ToArray() };
                if (reference is PortableExecutableReference { FilePath: { } file })
                {
                    file = Path.GetFullPath(file);
                    if (!inputs.Files.TryGetValue(file, out var fingerprint) || !fingerprint.Exists || fingerprint.Sha256 is null)
                        reasons.Add(projectPath + ": metadata bytes not in authenticated manifest: " + file);
                    references.Add(new { Kind = "pe", File = file, Hash = fingerprint?.Sha256, Bound = boundIdentity, Properties = properties });
                }
                else if (reference is CompilationReference source)
                {
                    // Match only CURRENT source project contexts, never a hidden external
                    // in-memory compilation with an equal assembly name.
                    var targets = project.ProjectReferences.Where(projectReference =>
                        compilations.TryGetValue(projectReference.ProjectId, out var target)
                        && (ReferenceEquals(target, source.Compilation)
                            || (target.Assembly.Identity.Equals(source.Compilation.Assembly.Identity)
                                && target.Options.Equals(source.Compilation.Options)
                                && target.SyntaxTrees.SequenceEqual(source.Compilation.SyntaxTrees)
                                && target.References.SequenceEqual(source.Compilation.References))))
                        .Select(projectReference => Relative(solution.GetProject(projectReference.ProjectId)!.FilePath!)).ToArray();
                    if (targets.Length != 1)
                        reasons.Add(projectPath + ": compilation reference context is not unique");
                    references.Add(new { Kind = "source", Targets = targets, Bound = boundIdentity, Properties = properties });
                }
                else
                {
                    reasons.Add(projectPath + ": unsupported realized metadata reference " + reference.GetType().FullName);
                    references.Add(new { Kind = "unsupported", Bound = boundIdentity, Properties = properties });
                }
            }

            var comparer = compilation.Options.AssemblyIdentityComparer;
            var desktopType = typeof(Compilation).Assembly.GetType("Microsoft.CodeAnalysis.DesktopAssemblyIdentityComparer");
            var desktopDefault = desktopType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null);
            if (!ReferenceEquals(comparer, AssemblyIdentityComparer.Default) && !ReferenceEquals(comparer, desktopDefault))
                reasons.Add(projectPath + ": custom assembly-identity comparer cannot be authenticated");
            var context = new
            {
                project.Name, project.AssemblyName, project.Language,
                AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName(),
                Options = CaptureOptionProperties(compilation.Options, reasons, projectPath),
                AssemblyIdentityComparer = comparer.GetType().AssemblyQualifiedName,
                ProjectReferences = project.ProjectReferences.Select(reference => new
                {
                    Project = Relative(solution.GetProject(reference.ProjectId)!.FilePath!),
                    Aliases = reference.Aliases.ToArray(), reference.EmbedInteropTypes
                }).ToArray(),
                References = references.ToArray()
            };
            states.Add(new(projectPath, IncrementalDigest.Json(context), treeStates.ToArray()));
        }
        return new(new(IncrementalGate.Version, indexId, await SemanticToolHashAsync(cancellationToken), IncrementalDigest.Json(inputs),
            states.ToArray(), reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()), searchable);
    }

    private static async Task<string> SemanticToolHashAsync(CancellationToken cancellationToken)
    {
        var assemblies = new[] { typeof(SolutionAnalysis).Assembly, typeof(Compilation).Assembly,
            typeof(CSharpCompilation).Assembly, typeof(SymbolFinder).Assembly }
            .Distinct().OrderBy(assembly => assembly.FullName, StringComparer.Ordinal).ToArray();
        var files = new HashSet<string>(SnapshotInputModel.PathComparer);
        foreach (var assembly in assemblies)
        {
            if (string.IsNullOrEmpty(assembly.Location))
                throw new AnalysisException("Incremental state requires inspectable tool/Roslyn assembly bytes.");
            files.Add(assembly.Location);
        }
        // Language services may load lazily on the FIRST reference search. Hash the
        // colocated Roslyn dependency files too, not only currently loaded assemblies.
        files.UnionWith(Directory.EnumerateFiles(Path.GetDirectoryName(typeof(SolutionAnalysis).Assembly.Location)!,
            "Microsoft.CodeAnalysis*.dll", SearchOption.TopDirectoryOnly));
        var values = new List<string>();
        foreach (var file in files.Order(SnapshotInputModel.PathComparer))
        {
            await using var stream = File.OpenRead(file);
            values.Add(file + ":" + Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        return IncrementalDigest.Json(values);
    }

    private static string CaptureOptionProperties(object options, List<string> reasons, string project)
    {
        // Resolver effects relevant to this supported query surface have ALREADY been
        // realized into regular syntax trees, assembly identity and reference bindings.
        // XML include text is not expanded by current method-reference queries.
        // TreeOptionsProvider governs diagnostics; current compilation is revalidated.
        // M6 recomputes declaration-owned XML trivia on every current semantic solution;
        // it never expands includes or invokes XmlReferenceResolver. This capture does
        // not authenticate external XML expansion, scripts, or other provider APIs.
        string[] realizedProviders = ["MetadataReferenceResolver", "XmlReferenceResolver", "SourceReferenceResolver",
            "StrongNameProvider", "SyntaxTreeOptionsProvider", "AssemblyIdentityComparer", "CurrentLocalTime"];
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (realizedProviders.Contains(property.Name, StringComparer.Ordinal)) continue;
            try
            {
                result[property.Name] = EncodeOption(property.GetValue(options));
            }
            catch (Exception exception) when (exception is NotSupportedException or TargetInvocationException)
            {
                reasons.Add(project + ": option cannot be closed: " + property.Name);
                result[property.Name] = "<unsupported>";
            }
        }
        return IncrementalDigest.Json(result);
    }

    private static string EncodeOption(object? value)
    {
        if (value is null) return "null";
        if (value is string text) return JsonSerializer.Serialize(text);
        if (value.GetType().IsEnum) return value.GetType().FullName + ":" + Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (value is IConvertible scalar) return value.GetType().FullName + ":" + scalar.ToString(CultureInfo.InvariantCulture);
        if (value is IEnumerable sequence)
        {
            var entries = sequence.Cast<object?>().ToArray();
            if (entries.Length > 0 && entries.All(entry => entry is not null && entry.GetType().IsGenericType
                    && entry.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>)))
                return JsonSerializer.Serialize(entries.Select(entry =>
                    EncodeOption(entry!.GetType().GetProperty("Key")!.GetValue(entry)) + ":"
                    + EncodeOption(entry.GetType().GetProperty("Value")!.GetValue(entry))).Order(StringComparer.Ordinal));
            return JsonSerializer.Serialize(entries.Select(EncodeOption));
        }
        throw new NotSupportedException("Unclosed option value: " + value.GetType().FullName);
    }
}
