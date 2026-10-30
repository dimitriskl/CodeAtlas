using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SnapshotInputModelTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-input-manifest-");
    private static readonly IReadOnlyDictionary<string, string> EnvironmentContext = new Dictionary<string, string> { ["BUILD_FLAVOR"] = "Debug" };
    private static BuildInputEvidence CompleteEvidence => new(true, true, true, true, true, true,
        [new("Library.csproj", ["net10.0"], "net10.0", [])], []);
    private InputDiscoveryScope SourceScope => new(root.FullName, [".cs", ".csproj", ".props", ".targets"],
        [".editorconfig"], [".codeatlas", "bin", "obj"]);

    [Fact]
    public async Task EqualFilesWithMissingEvidenceRemainUnknownAndPartial()
    {
        // Arrange
        var evidence = CompleteEvidence with { EvaluatedImportsCovered = false, ToolchainSelectionCovered = false };
        var manifest = await SnapshotInputModel.CaptureAsync([], [SourceScope], [], EnvironmentContext, evidence);

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.Equal("unknown", result.Status);
        Assert.False(result.TrackedInputsChanged);
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Contains(result.Coverage.Issues, issue => issue.Code == "imports_unverified");
        Assert.Contains(result.Coverage.Issues, issue => issue.Code == "toolchain_unverified");
    }

    [Fact]
    public void GeneratorsAndMultitargetsRemainUsableButNeverClaimCompleteCoverage()
    {
        // Arrange
        var evidence = CompleteEvidence with
        {
            Projects = [new("Library.csproj", ["net9.0", "net10.0"], "net10.0", ["Example.Generator"])]
        };

        // Act
        var coverage = SnapshotInputModel.Classify(evidence);

        // Assert
        Assert.Equal("partial", coverage.Status);
        Assert.Contains(coverage.Issues, issue => issue.Code == "source_generators_partial" && issue.Project == "Library.csproj");
        Assert.Contains(coverage.Issues, issue => issue.Code == "multi_target_partial" && issue.Project == "Library.csproj");
    }

    [Fact]
    public async Task ExternalBinaryReplacementIsDetectedDespiteEqualSizeAndTimestamp()
    {
        // Arrange
        var path = Path.Combine(root.FullName, "external.dll");
        await File.WriteAllTextAsync(path, "old-bytes");
        var stamp = File.GetLastWriteTimeUtc(path);
        var manifest = await SnapshotInputModel.CaptureAsync([path], [], [], EnvironmentContext, CompleteEvidence);
        await File.WriteAllTextAsync(path, "new-bytes");
        File.SetLastWriteTimeUtc(path, stamp);

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.Equal("stale", result.Status);
        Assert.Contains(path, result.ChangedInputs);
    }

    [Fact]
    public async Task AbsentAncestorCandidateBecomingPresentInvalidatesSnapshot()
    {
        // Arrange
        var path = Path.Combine(root.FullName, "Directory.Build.props");
        var manifest = await SnapshotInputModel.CaptureAsync([path], [], [], EnvironmentContext, CompleteEvidence);
        await File.WriteAllTextAsync(path, "<Project />");

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.False(manifest.Files[path].Exists);
        Assert.Equal("stale", result.Status);
        Assert.Contains(path, result.ChangedInputs);
    }

    [Fact]
    public async Task NewlyDiscoveredSourceInvalidatesWhileUnloadedOutputDoesNot()
    {
        // Arrange
        var manifest = await SnapshotInputModel.CaptureAsync([], [SourceScope], [], EnvironmentContext, CompleteEvidence);
        Directory.CreateDirectory(Path.Combine(root.FullName, "obj"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "obj", "Generated.cs"), "generated");
        var outputOnly = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);
        var source = Path.Combine(root.FullName, "NewSource.cs");
        await File.WriteAllTextAsync(source, "class Added {}");

        // Act
        var withSource = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.Equal("fresh", outputOnly.Status);
        Assert.Equal("stale", withSource.Status);
        Assert.Contains(source, withSource.ChangedInputs);
    }

    [Fact]
    public async Task EnvironmentChangesInvalidateWithoutPersistingValues()
    {
        // Arrange
        var manifest = await SnapshotInputModel.CaptureAsync([], [], [], EnvironmentContext, CompleteEvidence);
        var changed = new Dictionary<string, string> { ["BUILD_FLAVOR"] = "private-value-not-for-reports" };

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, changed);

        // Assert
        Assert.Equal("stale", result.Status);
        Assert.Contains("<environment>", result.ChangedInputs);
        Assert.DoesNotContain("private-value-not-for-reports", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(64, manifest.EnvironmentDigest.Length);
    }

    [Fact]
    public void EnvironmentDigestHasUnambiguousFieldBoundariesAndStableOrdering()
    {
        // Arrange
        var first = new Dictionary<string, string> { ["AB"] = "C", ["Z"] = "end" };
        var reordered = new Dictionary<string, string> { ["Z"] = "end", ["AB"] = "C" };
        var differentBoundaries = new Dictionary<string, string> { ["A"] = "BC", ["Z"] = "end" };

        // Act
        var hash = SnapshotInputModel.EnvironmentDigest(first);
        var reorderedHash = SnapshotInputModel.EnvironmentDigest(reordered);
        var boundaryHash = SnapshotInputModel.EnvironmentDigest(differentBoundaries);

        // Assert
        Assert.Equal(hash, reorderedHash);
        Assert.NotEqual(hash, boundaryHash);
    }

    [Fact]
    public async Task NewInstalledSdkDirectoryInvalidatesSelectionInventory()
    {
        // Arrange
        var sdk = Directory.CreateDirectory(Path.Combine(root.FullName, "sdk"));
        Directory.CreateDirectory(Path.Combine(sdk.FullName, "10.0.402"));
        var manifest = await SnapshotInputModel.CaptureAsync([], [], [sdk.FullName], EnvironmentContext, CompleteEvidence);
        Directory.CreateDirectory(Path.Combine(sdk.FullName, "10.0.403"));

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.Equal("stale", result.Status);
        Assert.Contains("<directory-inventory> " + sdk.FullName, result.ChangedInputs);
        // SDK inventory alone is not sufficient: the adapter must also capture selected toolchain files.
    }

    [Fact]
    public async Task ExplicitCustomAssetsPathIsTrackedEvenInsideExcludedOutputDirectory()
    {
        // Arrange
        var output = Directory.CreateDirectory(Path.Combine(root.FullName, "obj"));
        var assets = Path.Combine(output.FullName, "custom-assets.json");
        await File.WriteAllTextAsync(assets, "{}");
        var manifest = await SnapshotInputModel.CaptureAsync([assets], [SourceScope], [], EnvironmentContext, CompleteEvidence);
        await File.WriteAllTextAsync(assets, "{\"changed\":true}");

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, EnvironmentContext);

        // Assert
        Assert.Equal("stale", result.Status);
        Assert.Contains(assets, result.ChangedInputs);
    }

    [Fact]
    public async Task ActualWorkspaceCapturesExternalImportCustomAssetsReferencesAndSdk()
    {
        // Arrange
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "repo"));
        var imports = Path.Combine(root.FullName, "external.targets");
        var intermediate = Path.Combine(root.FullName, "custom-obj") + Path.DirectorySeparatorChar;
        await File.WriteAllTextAsync(imports, "<Project><PropertyGroup><DefineConstants>EXTERNAL</DefineConstants></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Example.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project=\"{imports}\" /></Project>");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Directory.Build.props"),
            $"<Project><PropertyGroup><BaseIntermediateOutputPath>{intermediate}</BaseIntermediateOutputPath></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Example.cs"), "public class Example { public void Run() {} }");
        var solution = Path.Combine(projectDirectory.FullName, "Example.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Example.csproj\" /></Solution>");
        var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = projectDirectory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(solution);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await errors);

        // Act
        var index = await SolutionAnalysis.CreateIndexAsync(solution);
        var manifest = index.InputManifest!;
        var unchanged = await SnapshotInputs.CheckFreshnessAsync(index);
        await File.AppendAllTextAsync(imports, "<!-- changed -->");
        var changed = await SnapshotInputs.CheckFreshnessAsync(index);

        // Assert
        Assert.NotNull(index.InputManifest);
        Assert.True(manifest.Files[imports].Exists);
        Assert.True(manifest.Files[Path.Combine(intermediate, "project.assets.json")].Exists);
        Assert.Contains(manifest.Files, pair => pair.Key.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase) && pair.Value.Exists);
        Assert.Contains(manifest.Files, pair => pair.Key.EndsWith("MSBuild.dll", StringComparison.OrdinalIgnoreCase) && pair.Value.Exists);
        Assert.Contains(manifest.DirectoryInventories, inventory => Path.GetFileName(inventory.Directory) == "sdk");
        Assert.All(manifest.Files.Where(pair => pair.Value.Exists), pair => Assert.Equal(64, pair.Value.Sha256!.Length));
        Assert.False(unchanged.IsStale, string.Join(", ", unchanged.ChangedFiles));
        Assert.Equal("partial", unchanged.Coverage.Status);
        Assert.Equal("unknown", unchanged.Status);
        Assert.True(changed.IsStale);
        Assert.Contains("../external.targets", changed.ChangedFiles);
    }

    [Fact]
    public void MalformedNestedManifestProducesFormatError()
    {
        // Arrange
        var malformed = new SnapshotInputManifest(1, [], [], new(), new(), [], new string('A', 64),
            CompleteEvidence with { Projects = null! }, new("complete", "supported-csharp-source-static-relations/v1", []));

        // Act
        var exception = Record.Exception(() => SnapshotInputModel.Validate(malformed));

        // Assert
        Assert.IsType<IndexFormatException>(exception);
        Assert.Equal("invalid_index", ((IndexFormatException)exception!).Code);
    }

    [Fact]
    public async Task ActualMultiTargetProjectAndSdkGeneratorsRemainExplicitlyPartial()
    {
        // Arrange
        var project = Path.Combine(root.FullName, "Multi.csproj");
        var solution = Path.Combine(root.FullName, "Multi.slnx");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup></Project>");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Multi.csproj\" /></Solution>");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "Example.cs"), "public class Example {}");
        var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(solution);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await errors);

        // Act
        var index = await SolutionAnalysis.CreateIndexAsync(solution);
        var freshness = await SnapshotInputs.CheckFreshnessAsync(index);

        // Assert
        Assert.Equal("unknown", freshness.Status);
        Assert.Equal("partial", freshness.Coverage.Status);
        Assert.Contains(freshness.Coverage.Issues, issue => issue.Code == "multi_target_partial");
        Assert.Contains(freshness.Coverage.Issues, issue => issue.Code == "source_generators_partial");
        Assert.Contains(index.InputManifest!.Evidence.Projects, evidence => evidence.TargetFrameworks.Length == 2);
    }

    private sealed class InputMutationProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    [Fact]
    public async Task ExternalImportChangedAfterDiscoveryCannotBindOldSemanticsToNewHashesOrReplacePreviousSnapshot()
    {
        // Arrange
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "repo"));
        var import = Path.Combine(root.FullName, "external.targets");
        var solution = Path.Combine(projectDirectory.FullName, "Example.slnx");
        await File.WriteAllTextAsync(import, "<Project><PropertyGroup><DefineConstants>BEFORE</DefineConstants></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Example.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project=\"{import}\" /></Project>");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Example.csproj\" /></Solution>");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Example.cs"),
            "public class Example {\n#if BEFORE\npublic void Before() {}\n#else\npublic void After() {}\n#endif\n}");
        var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = projectDirectory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(solution);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await errors);
        var previous = await SolutionAnalysis.CreateIndexAsync(solution);
        Assert.Contains(previous.Methods, method => method.Name == "Before");
        await IndexStore.SaveAsync(previous);
        var pointer = IndexStore.DefaultPath(solution);
        var previousPointer = await File.ReadAllBytesAsync(pointer);
        var mutated = false;
        var progress = new InputMutationProgress(value =>
        {
            if (value.Stage != IndexStage.FingerprintingInputs || mutated) return;
            mutated = true;
            File.WriteAllText(import, "<Project><PropertyGroup><DefineConstants>AFTER</DefineConstants></PropertyGroup></Project>");
        });

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            var replacement = await SolutionAnalysis.CreateIndexAsync(solution, progress: progress);
            await IndexStore.SaveAsync(replacement);
        });
        var retained = await IndexStore.LoadAsync(pointer);
        var rebuilt = await SolutionAnalysis.CreateIndexAsync(solution);

        // Assert
        Assert.True(mutated);
        Assert.IsType<AnalysisException>(failure);
        Assert.Contains("changed", failure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousPointer, await File.ReadAllBytesAsync(pointer));
        Assert.Equal(previous.IndexId, retained.IndexId);
        Assert.Contains(retained.Methods, method => method.Name == "Before");
        Assert.Contains(rebuilt.Methods, method => method.Name == "After");
        Assert.DoesNotContain(rebuilt.Methods, method => method.Name == "Before");
        Assert.False((await SnapshotInputs.CheckFreshnessAsync(rebuilt)).IsStale);
    }

    [Fact]
    public async Task UnreferencedShellVariablesDoNotInvalidateOrDiscloseValues()
    {
        // Arrange
        var indexing = new Dictionary<string, string>(SnapshotInputModel.PathComparer)
        {
            ["BUILD_FLAVOR"] = "Debug", ["PWD"] = @"C:\indexing-shell", ["OLDPWD"] = @"C:\old", ["CODEX_TOOL_CALL_ID"] = "index-call"
        };
        var manifest = await SnapshotInputModel.CaptureAsync([], [], [], indexing, CompleteEvidence, relevantEnvironment: ["BUILD_FLAVOR"]);
        var query = new Dictionary<string, string>(SnapshotInputModel.PathComparer)
        {
            ["BUILD_FLAVOR"] = "Debug", ["PWD"] = @"C:\query-shell", ["OLDPWD"] = @"C:\other", ["CODEX_TOOL_CALL_ID"] = "query-call"
        };

        // Act
        var fresh = await SnapshotInputModel.CheckAsync(manifest, query);
        query["BUILD_FLAVOR"] = "private-value-not-for-reports";
        var changed = await SnapshotInputModel.CheckAsync(manifest, query);
        query.Remove("BUILD_FLAVOR");
        var removed = await SnapshotInputModel.CheckAsync(manifest, query);

        // Assert
        Assert.Equal(["BUILD_FLAVOR"], manifest.RelevantEnvironment!);
        Assert.Equal("fresh", fresh.Status);
        Assert.DoesNotContain("<environment>", fresh.ChangedInputs);
        Assert.Equal("stale", changed.Status);
        Assert.Contains("<environment>", changed.ChangedInputs);
        Assert.Equal("stale", removed.Status);
        var serialized = System.Text.Json.JsonSerializer.Serialize(changed) + System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain("private-value-not-for-reports", serialized);
        Assert.DoesNotContain("query-shell", serialized);
    }

    [Fact]
    public async Task RelevantEnvironmentNamesAreUpperCasedOnlyOnWindows()
    {
        // Arrange
        var environment = new Dictionary<string, string> { ["BUILD_FLAVOR"] = "Debug" };
        var expected = OperatingSystem.IsWindows() ? "BUILD_FLAVOR" : "build_flavor";

        // Act
        var manifest = await SnapshotInputModel.CaptureAsync([], [], [], environment, CompleteEvidence, relevantEnvironment: ["build_flavor"]);

        // Assert
        Assert.Equal([expected], manifest.RelevantEnvironment!);
    }

    [Fact]
    public async Task LegacyManifestStillHashesTheWholeEnvironment()
    {
        // Arrange
        var manifest = await SnapshotInputModel.CaptureAsync([], [], [], EnvironmentContext, CompleteEvidence);
        var otherShell = new Dictionary<string, string>(EnvironmentContext, SnapshotInputModel.PathComparer) { ["PWD"] = @"C:\other-shell" };

        // Act
        var result = await SnapshotInputModel.CheckAsync(manifest, otherShell);

        // Assert
        Assert.Null(manifest.RelevantEnvironment);
        Assert.Contains("<environment>", result.ChangedInputs);
    }

    [Fact]
    public void MissingRelevantEnvironmentLoadsAsALegacyManifest()
    {
        // Arrange
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var manifest = new SnapshotInputManifest(1, [], [], new(SnapshotInputModel.PathComparer), new(SnapshotInputModel.PathComparer),
            [], new string('A', 64), CompleteEvidence, new("complete", "scope", []), ["BUILD_FLAVOR"]);
        var node = System.Text.Json.JsonSerializer.SerializeToNode(manifest, options)!.AsObject();
        node.Remove("relevantEnvironment");

        // Act
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SnapshotInputManifest>(node, options);

        // Assert
        Assert.NotNull(legacy);
        Assert.Null(legacy.RelevantEnvironment);
        SnapshotInputModel.Validate(legacy);
    }

    [Fact]
    public void ReferencedBuildVariablesAreKeptAndSdkOrShellVariablesAreNot()
    {
        // Arrange
        var xml = """
            <Project>
              <PropertyGroup Condition="'$(CODEATLAS_FIXTURE_FLAVOR)' == 'alpha'">
                <DefineConstants>$(CODEATLAS_FIXTURE_FLAVOR);$(Configuration)</DefineConstants>
              </PropertyGroup>
            </Project>
            $([System.Environment]::GetEnvironmentVariable('EXTRA_BUILD_INPUT'))
            """;
        var dotnetRoot = Path.Combine(root.FullName, "Program Files", "dotnet");
        var outputPath = Path.Combine(root.FullName, "repo", "obj", "project.assets.json");
        var sdkPath = Path.Combine(dotnetRoot, "sdk", "10.0.402", "Sdk.targets");
        var projectPath = Path.Combine(root.FullName, "repo", "Directory.Build.props");

        // Act
        var selected = BuildEnvironment.Select(BuildEnvironment.ReferencesIn(xml),
            ["CODEATLAS_FIXTURE_FLAVOR", "PWD", "PATH"],
            ["Configuration", "TargetFramework"]);
        var unset = BuildEnvironment.Select(["BUILD_FLAVOR", "Configuration"], ["PATH"], ["Configuration"]);

        // Assert
        Assert.Equal(["CODEATLAS_FIXTURE_FLAVOR", "EXTRA_BUILD_INPUT"], selected);
        Assert.Equal(["BUILD_FLAVOR"], unset);
        Assert.True(Path.IsPathFullyQualified(outputPath));
        Assert.True(Path.IsPathFullyQualified(sdkPath));
        Assert.True(Path.IsPathFullyQualified(projectPath));
        Assert.True(Path.IsPathFullyQualified(dotnetRoot));
        Assert.True(BuildEnvironment.IsOutputPath(outputPath));
        Assert.True(BuildEnvironment.IsSdkOrPackage(sdkPath, dotnetRoot));
        Assert.False(BuildEnvironment.IsSdkOrPackage(projectPath, dotnetRoot));
    }

    [Fact]
    public async Task DifferentShellDoesNotInvalidateAReferencedBuildVariableDoes()
    {
        // Arrange
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "shell-repo"));
        var project = Path.Combine(projectDirectory.FullName, "Example.csproj");
        var solution = Path.Combine(projectDirectory.FullName, "Example.slnx");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <DefineConstants>$(CODEATLAS_FIXTURE_FLAVOR)</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Example.cs"), "public class Example { public void Run() {} }");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Example.csproj\" /></Solution>");
        var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = projectDirectory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(solution);
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await output + await errors);
        }
        string[] names = ["PWD", "OLDPWD", "CODEX_TOOL_CALL_ID", "CODEATLAS_FIXTURE_FLAVOR"];
        var original = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("PWD", @"C:\indexing-shell");
            Environment.SetEnvironmentVariable("OLDPWD", @"C:\old");
            Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "index-call");
            Environment.SetEnvironmentVariable("CODEATLAS_FIXTURE_FLAVOR", "alpha");

            // Act
            var index = await SolutionAnalysis.CreateIndexAsync(solution);
            Environment.SetEnvironmentVariable("PWD", @"C:\query-shell");
            Environment.SetEnvironmentVariable("OLDPWD", @"C:\other");
            Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "query-call");
            var otherShell = await SnapshotInputs.CheckFreshnessAsync(index);
            Environment.SetEnvironmentVariable("CODEATLAS_FIXTURE_FLAVOR", "beta");
            var flavor = await SnapshotInputs.CheckFreshnessAsync(index);

            // Assert
            Assert.NotNull(index.InputManifest!.RelevantEnvironment);
            Assert.Contains("CODEATLAS_FIXTURE_FLAVOR", index.InputManifest.RelevantEnvironment);
            Assert.DoesNotContain(index.InputManifest.RelevantEnvironment, name =>
                name.Equals("PWD", StringComparison.OrdinalIgnoreCase)
                || name.Equals("OLDPWD", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CODEX_TOOL_CALL_ID", StringComparison.OrdinalIgnoreCase));
            Assert.False(otherShell.IsStale, string.Join(", ", otherShell.ChangedFiles));
            Assert.DoesNotContain("<environment>", otherShell.ChangedFiles);
            Assert.True(flavor.IsStale);
            Assert.Contains("<environment>", flavor.ChangedFiles);
            Assert.DoesNotContain("beta", System.Text.Json.JsonSerializer.Serialize(flavor));
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], original[i]);
        }
    }

    public void Dispose() => root.Delete(recursive: true);
}
