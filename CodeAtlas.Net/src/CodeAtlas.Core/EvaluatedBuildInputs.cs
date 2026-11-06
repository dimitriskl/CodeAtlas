using Microsoft.CodeAnalysis;
using Microsoft.Build.Evaluation;

namespace CodeAtlas;

public sealed partial class SolutionAnalysis
{
    private void EvaluateBuildInputs()
    {
        // Roslyn's out-of-process logger omits evaluated properties and import events.
        // Read actual evaluated imports/assets using MSBuild after locator registration.
        using var collection = new ProjectCollection(new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true", ["BuildingInsideVisualStudio"] = "true",
            ["BuildProjectReferences"] = "false", ["Configuration"] = "Debug", ["Platform"] = "AnyCPU"
        });
        var imports = new HashSet<string>(evaluatedImports, SnapshotInputModel.PathComparer);
        var names = new HashSet<string>(SnapshotInputModel.PathComparer);
        var dotnetRoot = selectedMSBuildPath is { } sdk ? Directory.GetParent(sdk)?.Parent?.FullName : null;
        foreach (var roslynProject in solution.Projects)
        {
            var path = Path.GetFullPath(roslynProject.FilePath!);
            var evaluated = collection.LoadProject(path);
            var targetFrameworks = evaluated.GetPropertyValue("TargetFrameworks");
            var targets = targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var selected = targets.FirstOrDefault(target => roslynProject.Name.EndsWith("(" + target + ")", StringComparison.Ordinal))
                ?? targets.FirstOrDefault();
            if (selected is not null)
            {
                evaluated.SetGlobalProperty("TargetFramework", selected);
                evaluated.ReevaluateIfNecessary();
            }
            evaluatedProperties[path] = evaluated.AllEvaluatedProperties.GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().EvaluatedValue, StringComparer.OrdinalIgnoreCase);
            if (targets.Length > 0) evaluatedProperties[path]["TargetFrameworks"] = targetFrameworks;
            imports.UnionWith(evaluated.Imports.Where(import => import.ImportedProject?.FullPath is not null)
                .Select(import => import.ImportedProject.FullPath));
            names.UnionWith(BuildEnvironment.FromProject(evaluated, dotnetRoot));
        }
        evaluatedImports = imports.ToArray();
        relevantEnvironment = names.Order(SnapshotInputModel.PathComparer).ToArray();
    }

    private (string[] Files, InputDiscoveryScope[] Scopes, string[] Inventories, BuildInputEvidence Evidence, string[] RelevantEnvironment) CollectBuildInputs(
        string[] roots, string[] initialFiles, bool detectGenerators = true)
    {
        EvaluateBuildInputs();
        var files = new HashSet<string>(initialFiles, SnapshotInputModel.PathComparer);
        files.UnionWith(evaluatedImports);
        var inventories = new HashSet<string>(SnapshotInputModel.PathComparer);
        var scopes = roots.Select(InputTracker.DiscoveryScope).ToList();
        var projects = new List<ProjectCoverageEvidence>();
        var issues = new List<CoverageIssue>();
        var propertiesCovered = true;
        var referencesCovered = true;
        var toolchainCovered = selectedMSBuildPath is not null;
        foreach (var project in solution.Projects)
        {
            var projectFile = Path.GetFullPath(project.FilePath!);
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!evaluatedProperties.TryGetValue(projectFile, out var properties))
            {
                properties = new(StringComparer.OrdinalIgnoreCase);
                propertiesCovered = false;
            }
            foreach (var property in new[] { "MSBuildAllProjects", "ProjectAssetsFile", "MSBuildProjectExtensionsPath", "NuGetLockFilePath", "AppConfig", "ApplicationManifest", "ApplicationIcon", "AssemblyOriginatorKeyFile" })
            {
                if (!properties.TryGetValue(property, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var path = Path.GetFullPath(item, projectDirectory);
                    if (property == "MSBuildProjectExtensionsPath")
                    {
                        files.Add(Path.Combine(path, "project.assets.json"));
                        files.Add(Path.Combine(path, Path.GetFileName(projectFile) + ".nuget.g.props"));
                        files.Add(Path.Combine(path, Path.GetFileName(projectFile) + ".nuget.g.targets"));
                    }
                    else files.Add(path);
                }
            }
            foreach (var reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference { FilePath: { } referenceFile }) files.Add(referenceFile);
                else referencesCovered = false;
            }
            var generators = new List<string>();
            foreach (var analyzer in project.AnalyzerReferences)
            {
                if (analyzer.FullPath is { } analyzerFile) files.Add(analyzerFile);
                else issues.Add(new("analyzer_input_untracked", "An analyzer reference has no on-disk input.", Relative(projectFile)));
                if (detectGenerators && analyzer.GetGenerators(LanguageNames.CSharp).Length != 0)
                    generators.Add(analyzer.FullPath ?? analyzer.Display ?? "unknown generator");
            }
            var targets = properties.GetValueOrDefault("TargetFrameworks", properties.GetValueOrDefault("TargetFramework", ""))
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            projects.Add(new(Relative(projectFile), targets, properties.GetValueOrDefault("TargetFramework"), generators.ToArray()));
            if (targets.Length == 0)
                issues.Add(new("target_context_unverified", "The evaluated target framework could not be established.", Relative(projectFile)));
            // A custom MSBuild target may read arbitrary files or discover conditional imports.
            // Capturing loaded inputs does not prove that those unreported inputs are covered.
            issues.Add(new("dynamic_build_inputs_unverified", "Arbitrary task inputs and conditional import discovery are outside the tracked input model.", Relative(projectFile)));
        }
        if (selectedMSBuildPath is { } sdk)
        {
            sdk = Path.GetFullPath(sdk);
            files.UnionWith(new[] { "MSBuild.dll", "Microsoft.Build.dll", "Microsoft.Build.Framework.dll", "Microsoft.Build.Utilities.Core.dll",
                "Roslyn/bincore/csc.dll", "Roslyn/bincore/Microsoft.CodeAnalysis.dll", "Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll" }
                .Select(name => Path.Combine(sdk, name)));
            var sdkRoot = Directory.GetParent(sdk)?.FullName;
            if (sdkRoot is not null)
            {
                inventories.Add(sdkRoot);
                var dotnetRoot = Directory.GetParent(sdkRoot)?.FullName;
                if (dotnetRoot is not null)
                {
                    files.Add(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
                    inventories.Add(Path.Combine(dotnetRoot, "host", "fxr"));
                    inventories.Add(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"));
                }
            }
        }
        issues.Add(new("evaluation_context_replayed", "MSBuild inputs were evaluated with explicit design-time defaults; custom workspace evaluation context is not fully verifiable."));
        return (files.Order(SnapshotInputModel.PathComparer).ToArray(), scopes.ToArray(), inventories.Order(SnapshotInputModel.PathComparer).ToArray(),
            new(true, propertiesCovered, referencesCovered, toolchainCovered, true, false, projects.ToArray(), issues.ToArray()),
            relevantEnvironment);
    }
}
