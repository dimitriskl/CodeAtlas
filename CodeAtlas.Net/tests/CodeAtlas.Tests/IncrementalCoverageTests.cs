using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CodeAtlas.Tests;

public sealed class IncrementalCoverageTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPhysicalEditsRefreshEveryConditionalProjectOrigin(bool conditional)
    {
        // Arrange
        var fixture = await CoverageFixture.CreateAsync(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Linked.cs"),
                (conditional ? "#if CALLER\nnamespace LinkedCaller;\n#else\nnamespace LinkedLibrary;\n#endif\n" : "namespace LinkedSource;\n")
                + "internal static class Calls { internal static int Run() => Sample.Calculator.Calculate(1); }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Library", "Library.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../Linked.cs\" Link=\"Linked.cs\" /></ItemGroup></Project>");
            var caller = Path.Combine(root, "Caller", "Caller.csproj");
            await File.WriteAllTextAsync(caller, (await File.ReadAllTextAsync(caller)).Replace("</Project>",
                "<PropertyGroup><DefineConstants>$(DefineConstants);CALLER</DefineConstants></PropertyGroup>"
                + "<ItemGroup><Compile Include=\"../Linked.cs\" Link=\"Linked.cs\" /></ItemGroup></Project>", StringComparison.Ordinal));
        });
        var shared = Path.Combine(fixture.Root, "Linked.cs");
        await File.WriteAllTextAsync(shared, (await File.ReadAllTextAsync(shared)).Replace(
            "Sample.Calculator.Calculate(1)", "\n Sample.Calculator.Calculate(\"linked\").Length", StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);
        var oldLinked = fixture.Baseline.References.Values.SelectMany(references => references)
            .Where(reference => reference.Location.File == "Linked.cs").ToArray();

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        var linked = actual.References.Values.SelectMany(references => references)
            .Where(reference => reference.Location.File == "Linked.cs").ToArray();

        // Assert
        EqualCurrentSnapshot(expected, actual);
        Assert.Equal(conditional ? "full" : "incremental-body", actual.Refresh!.Mode);
        if (conditional)
            Assert.Equal("declaration_surface_changed", actual.Refresh.FallbackReason);
        else
        {
            Assert.Equal(2, actual.Refresh.ChangedDocumentContexts);
            Assert.True(actual.Refresh.ReusedReferences > 0);
            Assert.Equal(actual.Methods.Length, actual.Refresh.ReferenceSearches);
        }
        Assert.Equal(new[] { "Caller/Caller.csproj", "Library/Library.csproj" }, linked.Select(reference => reference.Project).Distinct().Order(StringComparer.Ordinal));
        Assert.All(linked, reference => Assert.Contains("System.String", reference.DefinitionId, StringComparison.Ordinal));
        Assert.All(oldLinked, reference => Assert.Contains("System.Int32", reference.DefinitionId, StringComparison.Ordinal));
        Assert.DoesNotContain(actual.References.Values.SelectMany(references => references), reference =>
            reference.Location.File == "Linked.cs" && reference.DefinitionId.Contains("System.Int32", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("signature", "declaration_surface_changed")]
    [InlineData("using", "declaration_surface_changed")]
    [InlineData("cref", "declaration_surface_changed")]
    [InlineData("add", "document_inventory_changed")]
    [InlineData("delete", "document_inventory_changed")]
    [InlineData("rename", "document_inventory_changed")]
    [InlineData("project-options", "compilation_context_changed")]
    public async Task StructuralAndConfigurationEditsFallBackAndEqualTheCurrentFullSnapshot(string edit, string reason)
    {
        // Arrange
        var fixture = await CoverageFixture.CreateAsync(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Library", "Extra.cs"),
                "namespace Sample; internal static class Extra { internal static int Value() => 10; }\n");
            var calculator = Path.Combine(root, "Library", "Calculator.cs");
            await File.WriteAllTextAsync(calculator, (await File.ReadAllTextAsync(calculator)).Replace(
                "public static int LocalCaller()", "/// <see cref=\"Calculate(int)\"/>\n    public static int LocalCaller()", StringComparison.Ordinal));
        });
        switch (edit)
        {
            case "signature":
                await fixture.ReplaceCalculatorAsync("Calculate(int value)", "Calculate(int value, int extra = 0)");
                // Keep the existing method-group conversion valid after adding an optional parameter.
                var consumer = Path.Combine(fixture.Root, "Caller", "Consumer.cs");
                await File.WriteAllTextAsync(consumer, (await File.ReadAllTextAsync(consumer)).Replace(
                    "=> Calculator.Calculate;", "=> value => Calculator.Calculate(value);", StringComparison.Ordinal));
                break;
            case "using":
                await fixture.ReplaceCalculatorAsync("namespace Sample;", "using System.Text;\nnamespace Sample;");
                break;
            case "cref":
                await fixture.ReplaceCalculatorAsync("cref=\"Calculate(int)\"", "cref=\"Calculate(string)\"");
                break;
            case "add":
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Library", "Added.cs"),
                    "namespace Sample; internal class Added { internal int Call() => Calculator.Calculate(4); }\n");
                break;
            case "delete":
                File.Delete(Path.Combine(fixture.Root, "Library", "Extra.cs"));
                break;
            case "rename":
                File.Move(Path.Combine(fixture.Root, "Library", "Extra.cs"), Path.Combine(fixture.Root, "Library", "Renamed.cs"));
                break;
            case "project-options":
                var project = Path.Combine(fixture.Root, "Library", "Library.csproj");
                await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><CheckForOverflowUnderflow>true</CheckForOverflowUnderflow></PropertyGroup></Project>");
                break;
        }
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);

        // Assert
        EqualCurrentSnapshot(expected, actual);
        Assert.Equal("full", actual.Refresh!.Mode);
        Assert.Equal(reason, actual.Refresh.FallbackReason);
        Assert.Equal(0, actual.Refresh.ReusedReferences);
        Assert.Equal(actual.Methods.Length, actual.Refresh.ReferenceSearches);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("body")]
    [InlineData("multi-body")]
    [InlineData("declaration")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task RealizedGeneratorOutputsAreComparedBeforeReferenceReuse(string mode)
    {
        // Arrange
        var fixture = await CoverageFixture.CreateAsync(async root =>
        {
            var analyzerName = "ChangingGenerator_" + mode + ".dll";
            var analyzer = Path.Combine(root, analyzerName);
            EmitGenerator(analyzer, mode);
            await File.WriteAllTextAsync(Path.Combine(root, "Library", "Library.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Analyzer Include=\"../" + analyzerName + "\" /></ItemGroup></Project>");
        });
        await fixture.ReplaceCalculatorAsync("Calculate(1)", "Calculate(\"generator-v2\").Length");
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);
        var before = fixture.Baseline.IncrementalState!.Projects.SelectMany(project => project.Trees)
            .Where(tree => tree.OriginPath.EndsWith("Changing.g.cs", StringComparison.Ordinal)).ToArray();
        var oldGeneratedReferences = fixture.Baseline.References.Values.SelectMany(references => references)
            .Where(reference => reference.Location.File.EndsWith("Changing.g.cs", StringComparison.Ordinal)).ToArray();

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        var generated = actual.IncrementalState!.Projects.SelectMany(project => project.Trees)
            .Where(tree => tree.OriginPath.EndsWith("Changing.g.cs", StringComparison.Ordinal)).ToArray();
        var generatedReferences = actual.References.Values.SelectMany(references => references)
            .Where(reference => reference.Location.File.EndsWith("Changing.g.cs", StringComparison.Ordinal)).ToArray();
        var orderedInventoryChanged = fixture.Baseline.IncrementalState!.Projects.Zip(actual.IncrementalState.Projects)
            .Any(pair => pair.First.Trees.Length != pair.Second.Trees.Length
                || pair.First.Trees.Zip(pair.Second.Trees).Any(trees => trees.First.Key != trees.Second.Key
                    || trees.First.OriginPath != trees.Second.OriginPath || trees.First.PhysicalPath != trees.Second.PhysicalPath
                    || trees.First.Searchable != trees.Second.Searchable));
        output.WriteLine("generator={0}; refresh={1}; reason={2}; changedContexts={3}; generatedSearchable={4}; generatedReferences={5}",
            mode, actual.Refresh!.Mode, actual.Refresh.FallbackReason ?? "none", actual.Refresh.ChangedDocumentContexts,
            string.Join(",", generated.Select(tree => tree.Searchable)), generatedReferences.Length);
        output.WriteLine("baselineUnsupported={0}; currentUnsupported={1}",
            string.Join(" | ", fixture.Baseline.IncrementalState!.UnsupportedReasons),
            string.Join(" | ", actual.IncrementalState!.UnsupportedReasons));
        foreach (var (oldProject, newProject) in fixture.Baseline.IncrementalState.Projects.Zip(actual.IncrementalState.Projects))
        {
            output.WriteLine("project={0}; baselineTrees={1}; currentTrees={2}", oldProject.Project, oldProject.Trees.Length, newProject.Trees.Length);
            foreach (var (oldTree, newTree) in oldProject.Trees.Zip(newProject.Trees))
                if (oldTree.Key != newTree.Key || oldTree.OriginPath != newTree.OriginPath
                    || oldTree.PhysicalPath != newTree.PhysicalPath || oldTree.Searchable != newTree.Searchable)
                    output.WriteLine("treeIdentityDifference baseline={0}; current={1}",
                        JsonSerializer.Serialize(new { oldTree.Key, oldTree.OriginPath, oldTree.PhysicalPath, oldTree.Searchable }),
                        JsonSerializer.Serialize(new { newTree.Key, newTree.OriginPath, newTree.PhysicalPath, newTree.Searchable }));
        }

        // Assert
        EqualCurrentSnapshot(expected, actual);
        Assert.NotNull(actual.Refresh);
        if (mode is "add" or "remove")
        {
            Assert.Equal(mode == "add" ? 0 : 1, before.Length);
            Assert.Equal(mode == "remove" ? 0 : 1, generated.Length);
            Assert.Equal("full", actual.Refresh.Mode);
            Assert.Equal("document_inventory_changed", actual.Refresh.FallbackReason);
            Assert.Equal(0, actual.Refresh.ReusedReferences);
        }
        else
        {
            var oldTree = Assert.Single(before);
            var newTree = Assert.Single(generated);
            if (mode == "unchanged")
            {
                Assert.Equal(oldTree.ContentHash, newTree.ContentHash);
                Assert.Equal(oldTree.SurfaceHash, newTree.SurfaceHash);
                Assert.Equal("incremental-body", actual.Refresh.Mode);
                Assert.Equal(1, actual.Refresh.ChangedDocumentContexts);
            }
            else
            {
                Assert.NotEqual(oldTree.ContentHash, newTree.ContentHash);
                if (mode == "declaration")
                {
                    Assert.NotEqual(oldTree.SurfaceHash, newTree.SurfaceHash);
                    Assert.Equal("full", actual.Refresh.Mode);
                    Assert.Equal("declaration_surface_changed", actual.Refresh.FallbackReason);
                    Assert.Equal(0, actual.Refresh.ReusedReferences);
                }
                else
                {
                    Assert.Equal(oldTree.SurfaceHash, newTree.SurfaceHash);
                    if (mode == "multi-body" && orderedInventoryChanged)
                    {
                        Assert.Equal("full", actual.Refresh.Mode);
                        Assert.Equal("document_inventory_changed", actual.Refresh.FallbackReason);
                        Assert.Equal(0, actual.Refresh.ReusedReferences);
                    }
                    else if (oldTree.Searchable && newTree.Searchable)
                    {
                        Assert.Equal("incremental-body", actual.Refresh.Mode);
                        Assert.Equal(2, actual.Refresh.ChangedDocumentContexts);
                        Assert.Equal(actual.Methods.Length, actual.Refresh.ReferenceSearches);
                    }
                    else
                    {
                        Assert.Equal("full", actual.Refresh.Mode);
                        Assert.Equal("unsearchable_changed_tree", actual.Refresh.FallbackReason);
                    }
                }
            }
        }
        if (mode == "add") Assert.Empty(oldGeneratedReferences);
        else
        {
            var oldReference = Assert.Single(oldGeneratedReferences);
            Assert.Contains("System.Int32", oldReference.DefinitionId, StringComparison.Ordinal);
            Assert.Equal("invocation", oldReference.Kind);
            Assert.Equal("Library/Library.csproj", oldReference.Project);
        }
        if (mode == "remove") Assert.Empty(generatedReferences);
        else
        {
            var newReference = Assert.Single(generatedReferences);
            Assert.Contains(mode is "body" or "multi-body" or "declaration" ? "System.String" : "System.Int32", newReference.DefinitionId, StringComparison.Ordinal);
            Assert.Equal("invocation", newReference.Kind);
            Assert.Equal("Library/Library.csproj", newReference.Project);
        }
        Assert.Equal("partial", actual.InputManifest!.Coverage.Status);
        Assert.Contains(actual.InputManifest.Coverage.Issues, issue => issue.Code == "source_generators_partial");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledRefreshPublicationKeepsTheOldManifestAndPayload(bool cancel)
    {
        // Arrange
        var fixture = await CoverageFixture.CreateAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.Manifest);
        await fixture.ReplaceCalculatorAsync("Calculate(1)", "Calculate(\"publication\").Length");
        var refreshed = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        using var cancellation = new CancellationTokenSource();
        Task StopPublication(Stream _, CancellationToken token)
        {
            if (!cancel) throw new IOException("Injected refresh timing-artifact failure.");
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.PublishAsync(refreshed, StopPublication, cancellation.Token));
        var retained = await IndexStore.LoadSnapshotAsync(fixture.Manifest);

        // Assert
        Assert.Equal("incremental-body", refreshed.Refresh!.Mode);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.Equal("snapshot_io_failed", Assert.IsType<IndexFormatException>(failure).Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Equal(fixture.Baseline.IndexId, retained.Index.IndexId);
        EqualCurrentSnapshot(fixture.Baseline, retained.Index);
    }

    [Fact]
    public async Task PublishedRefreshRejectsOldPagingTokensAndServesItsCompleteCurrentPayload()
    {
        // Arrange
        var fixture = await CoverageFixture.CreateAsync();
        var first = QueryPaging.Methods(fixture.Baseline.Methods, new QueryRequest(Limit: 1), fixture.Baseline.IndexId);
        await fixture.ReplaceCalculatorAsync("Calculate(1)", "Calculate(\"paging\").Length");
        var refreshed = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        await IndexStore.PublishAsync(refreshed);
        var loaded = await IndexStore.LoadSnapshotAsync(fixture.Manifest);
        var continuationFailure = Record.Exception(() => QueryPaging.Methods(loaded.Index.Methods,
            new QueryRequest(Limit: 1, Offset: first.Pagination.NextOffset!.Value, Snapshot: first.Pagination.SnapshotId), loaded.Index.IndexId));

        // Assert
        EqualCurrentSnapshot(expected, loaded.Index);
        Assert.Equal(refreshed.IndexId, loaded.Index.IndexId);
        Assert.Equal(refreshed.Refresh, loaded.Index.Refresh);
        Assert.NotEqual(fixture.Baseline.IndexId, loaded.Index.IndexId);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(continuationFailure).Code);
    }

    private static void EqualCurrentSnapshot(CodebaseIndex expected, CodebaseIndex actual)
    {
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal(expected.InputRoots, actual.InputRoots);
        Assert.Equal(expected.ExplicitInputs, actual.ExplicitInputs);
        Assert.Equal(expected.Fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.Fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(JsonSerializer.Serialize(expected.InputManifest), JsonSerializer.Serialize(actual.InputManifest));
    }

    private static void EmitGenerator(string path, string mode)
    {
        const string source = """
            using System.Linq;
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class ChangingGenerator : ISourceGenerator
            {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context)
                {
                    const string mode = "__MODE__";
                    var changed = context.Compilation.SyntaxTrees.Any(tree => tree.ToString().Contains("generator-v2"));
                    // Use one output for body/declaration scenarios: Roslyn may reorder
                    // multiple generated trees between independent workspace loads.
                    if (mode == "add" || mode == "remove" || mode == "multi-body")
                        context.AddSource("Stable.g.cs", "namespace Generated { internal static class Stable { internal static int Value() => 3; } }");
                    if ((mode == "add" && !changed) || (mode == "remove" && changed)) return;
                    var name = mode == "declaration" && changed ? "After" : "Before";
                    var expression = changed && (mode == "body" || mode == "multi-body" || mode == "declaration")
                        ? "Sample.Calculator.Calculate(\"generated\").Length" : "Sample.Calculator.Calculate(7)";
                    context.AddSource("Changing.g.cs", "namespace Generated { internal static class Output { internal static int " + name + "() => " + expression + "; } }");
                }
            }
            """;
        var assemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Compilation).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("ChangingGenerator_" + mode, [CSharpSyntaxTree.ParseText(source.Replace("__MODE__", mode, StringComparison.Ordinal))],
            assemblies.Select(assembly => MetadataReference.CreateFromFile(assembly)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
    }

    private sealed class CoverageFixture(string root)
    {
        internal string Root => root;
        internal string Solution => Path.Combine(root, "Sample.slnx");
        internal string Manifest => IndexStore.DefaultPath(Solution);
        internal CodebaseIndex Baseline { get; private set; } = null!;

        internal async Task ReplaceCalculatorAsync(string before, string after)
        {
            var path = Path.Combine(root, "Library", "Calculator.cs");
            var source = await File.ReadAllTextAsync(path);
            if (!source.Contains(before, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture edit did not match source.");
            await File.WriteAllTextAsync(path, source.Replace(before, after, StringComparison.Ordinal));
        }

        internal static async Task<CoverageFixture> CreateAsync(Func<string, Task>? prepare = null)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
            if (repository is null) throw new InvalidOperationException("Required checkout not found.");
            var root = Path.Combine(repository.FullName, ".codeatlas", "validation", "m05-tests", "coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var relative in new[] { "Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs", "Library/packages.lock.json",
                         "Caller/Caller.csproj", "Caller/Consumer.cs", "Caller/packages.lock.json" })
            {
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repository.FullName, "tests", "Fixtures", relative), target);
            }
            foreach (var name in new[] { "Directory.Build.props", "Version.props" }) File.Copy(Path.Combine(repository.FullName, name), Path.Combine(root, name));
            if (prepare is not null) await prepare(root);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "restore", "Sample.slnx", "--locked-mode" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await output + await error);
            var fixture = new CoverageFixture(root) { Baseline = await SolutionAnalysis.CreateIndexAsync(Path.Combine(root, "Sample.slnx")) };
            await IndexStore.SaveAsync(fixture.Baseline);
            return fixture;
        }
    }
}
