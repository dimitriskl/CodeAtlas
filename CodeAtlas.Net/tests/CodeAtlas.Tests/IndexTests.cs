using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class IndexedSolution : IAsyncLifetime
{
    private DirectoryInfo temporary = null!;
    public string Root { get; private set; } = "";
    public string Parent => temporary.FullName;
    public string IndexPath => IndexStore.DefaultPath(Path.Combine(Root, "Sample.slnx"));
    public CodebaseIndex Index { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        if (repository is null)
            throw new InvalidOperationException("Repository not found.");
        temporary = Directory.CreateTempSubdirectory("codeatlas-index-tests-");
        Root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "repo")).FullName;
        var fixture = Path.Combine(repository.FullName, "tests", "Fixtures");
        foreach (var relative in new[] { "Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs", "Library/packages.lock.json", "Caller/Caller.csproj", "Caller/Consumer.cs", "Caller/packages.lock.json" })
        {
            var target = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixture, relative), target);
        }
        File.Copy(Path.Combine(repository.FullName, "Directory.Build.props"), Path.Combine(Root, "Directory.Build.props"));
        File.Copy(Path.Combine(repository.FullName, "Version.props"), Path.Combine(Root, "Version.props"));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add("Sample.slnx");
        start.ArgumentList.Add("--locked-mode");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        Index = await SolutionAnalysis.CreateIndexAsync(Path.Combine(Root, "Sample.slnx"));
        await IndexStore.SaveAsync(Index);
    }

    public Task DisposeAsync()
    {
        temporary.Delete(recursive: true);
        return Task.CompletedTask;
    }
}

public sealed class IndexTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private sealed class CapturedProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidWorkerCountIsRejectedBeforeLoadingSolution(int workers)
    {
        // Arrange
        const string missingSolution = "does-not-exist.slnx";

        // Act
        var exception = await Record.ExceptionAsync(() => SolutionAnalysis.CreateIndexAsync(
            missingSolution, referenceWorkers: workers));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public async Task IndexProgressReportsStagesAndActualCompletedMethodCounts()
    {
        // Arrange
        var events = new System.Collections.Concurrent.ConcurrentQueue<IndexProgress>();
        var progress = new CapturedProgress(events.Enqueue);

        // Act
        var index = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution, progress: progress);

        // Assert
        var stages = events.Select(e => e.Stage).Distinct().ToArray();
        Assert.Equal(new[] { IndexStage.ScanningInputs, IndexStage.LoadingProjects, IndexStage.ValidatingProjects,
            IndexStage.FingerprintingInputs, IndexStage.DiscoveringSymbols, IndexStage.FindingReferences,
            IndexStage.CollectingTypes, IndexStage.VerifyingInputs }, stages);
        var references = events.Where(e => e.Stage == IndexStage.FindingReferences).ToArray();
        Assert.Equal(Enumerable.Range(0, index.Methods.Length + 1), references.Select(e => e.Completed!.Value));
        Assert.All(references, e => Assert.Equal(index.Methods.Length, e.Total));
        Assert.Equal(index.Projects.Length, events.Last(e => e.Stage == IndexStage.ValidatingProjects).Completed);
        Assert.Equal(fixture.Index.Methods, index.Methods);
        foreach (var method in index.Methods)
            Assert.Equal(fixture.Index.References[method.Id], index.References[method.Id]);
    }

    [Fact]
    public async Task CancellationAtReferenceProgressStopsBeforeCompletingMethods()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var events = new List<IndexProgress>();
        var progress = new CapturedProgress(e =>
        {
            lock (events)
                events.Add(e);
            if (e.Stage == IndexStage.FindingReferences)
                cancellation.Cancel();
        });

        // Act
        var exception = await Record.ExceptionAsync(() => SolutionAnalysis.CreateIndexAsync(
            fixture.Index.Solution, cancellation.Token, progress));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(0, Assert.Single(events, e => e.Stage == IndexStage.FindingReferences).Completed);
        Assert.DoesNotContain(events, e => e.Stage == IndexStage.VerifyingInputs || e.Stage == IndexStage.Completed);
    }

    [Fact]
    public async Task CancellationAfterFirstReferenceCompletionPreservesPublishedIndex()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var previousIndex = await File.ReadAllBytesAsync(fixture.IndexPath);
        var events = new System.Collections.Concurrent.ConcurrentQueue<IndexProgress>();
        var progress = new CapturedProgress(e =>
        {
            events.Enqueue(e);
            if (e.Stage == IndexStage.FindingReferences && e.Completed == 1)
                cancellation.Cancel();
        });
        async Task RebuildIndexAsync()
        {
            var index = await SolutionAnalysis.CreateIndexAsync(
                fixture.Index.Solution, cancellation.Token, progress, referenceWorkers: 2);
            await IndexStore.SaveAsync(index, cancellation.Token);
        }

        // Act
        var exception = await Record.ExceptionAsync(RebuildIndexAsync);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Contains(events, e => e.Stage == IndexStage.FindingReferences && e.Completed == 1);
        Assert.DoesNotContain(events, e => e.Stage == IndexStage.FindingReferences && e.Completed == e.Total);
        Assert.DoesNotContain(events, e => e.Stage is IndexStage.CollectingTypes or IndexStage.VerifyingInputs or IndexStage.Completed);
        Assert.Equal(previousIndex, await File.ReadAllBytesAsync(fixture.IndexPath));
    }

    [Fact]
    public async Task DesignTimeBuildWarningIsPreservedWithoutRejectingIndex()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, "Directory.Build.targets");
        await File.WriteAllTextAsync(path, """
            <Project><Target Name="AnalysisWarning" BeforeTargets="ResolveReferences">
              <Warning Code="ATLAS001" Text="CodeAtlas warning-only fixture" />
            </Target></Project>
            """);
        try
        {
            // Act
            var index = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);

            // Assert
            Assert.Equal(6, index.Methods.Length);
            Assert.Contains(index.Warnings, warning => warning.Contains("CodeAtlas warning-only fixture"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DesignTimeBuildErrorStillRejectsIndexEvenWithIdenticalWarningText()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, "Directory.Build.targets");
        await File.WriteAllTextAsync(path, """
            <Project><Target Name="AnalysisError" BeforeTargets="ResolveReferences">
              <Warning Code="ATLAS001" Text="CodeAtlas shared diagnostic text" />
              <Error Code="ATLAS002" Text="CodeAtlas shared diagnostic text" />
            </Target></Project>
            """);
        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution));

            // Assert
            var analysis = Assert.IsType<AnalysisException>(exception);
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Contains("CodeAtlas shared diagnostic text"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CompilationErrorStillRejectsIndexWhenBuildHasOnlyWarnings()
    {
        // Arrange
        var targets = Path.Combine(fixture.Root, "Directory.Build.targets");
        var source = Path.Combine(fixture.Root, "Library", "Invalid.cs");
        await File.WriteAllTextAsync(targets, """
            <Project><Target Name="AnalysisWarning" BeforeTargets="ResolveReferences">
              <Warning Code="ATLAS001" Text="CodeAtlas warning-only fixture" />
            </Target></Project>
            """);
        await File.WriteAllTextAsync(source, "public class Invalid { public MissingType Run() => null; }");
        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution));

            // Assert
            var analysis = Assert.IsType<AnalysisException>(exception);
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Contains("CS0246"));
        }
        finally
        {
            File.Delete(targets);
            File.Delete(source);
        }
    }

    [Fact]
    public async Task RoundTripIncludesWholeSolutionAndMatchesLiveQueries()
    {
        var index = await IndexStore.LoadAsync(fixture.IndexPath);
        Assert.Equal(2, index.Projects.Length);
        Assert.Equal(CodeAtlasVersion.Current, index.ToolVersion);
        Assert.Equal(new[] { "Library/Library.csproj" }, index.Projects.Single(p => p.Name == "Caller").Dependencies);
        Assert.Contains(index.Types, t => t.Name == "Sample.Calculator");
        Assert.Contains(index.Types, t => t.Name == "Client.Consumer");
        Assert.Equal(6, index.Methods.Length);
        using var live = await SolutionAnalysis.LoadAsync(index.Solution);
        Assert.Equal(await live.FindMethodsAsync("Calculate"), IndexStore.FindMethods(index, "Calculate"));
        foreach (var method in index.Methods)
            Assert.Equal(await live.FindReferencesAsync(method.Id), IndexStore.FindReferences(index, method.Id));
        Assert.False((await IndexStore.CheckFreshnessAsync(index)).IsStale);
        Assert.Empty(IndexStore.FindMethods(index, "MissingMethod"));
        Assert.Throws<AnalysisException>(() => IndexStore.FindReferences(index, "missing-id"));
    }

    [Fact]
    public async Task OverviewIsReadableAndIdentifiesTheSnapshot()
    {
        var overview = await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".codeatlas", "overview.md"));
        Assert.Contains(fixture.Index.IndexId, overview);
        Assert.Contains($"CodeAtlas version: **{CodeAtlasVersion.Current}**", overview);
        Assert.Contains("Sample.Calculator", overview);
        Assert.Contains("Client.Consumer", overview);
        Assert.Contains("Project dependencies: `Library/Library.csproj`", overview);
        Assert.Contains("index-status", overview);
        Assert.Contains("snapshot", overview);
    }

    [Fact]
    public async Task HashDetectsSourceChangeEvenWithSameSizeAndTimestamp()
    {
        var path = Path.Combine(fixture.Root, "Library", "Calculator.cs");
        var original = await File.ReadAllTextAsync(path);
        var timestamp = File.GetLastWriteTimeUtc(path);
        try
        {
            await File.WriteAllTextAsync(path, original.Replace("value + 1", "value + 2"));
            File.SetLastWriteTimeUtc(path, timestamp);
            var status = await IndexStore.CheckFreshnessAsync(fixture.Index);
            Assert.True(status.IsStale);
            Assert.Contains("Library/Calculator.cs", status.ChangedFiles);
            await Assert.ThrowsAsync<StaleIndexException>(() => IndexStore.RequireFreshAsync(fixture.Index));
        }
        finally
        {
            await File.WriteAllTextAsync(path, original);
            File.SetLastWriteTimeUtc(path, timestamp);
        }
    }

    [Theory]
    [InlineData("Library/NewFile.cs")]
    [InlineData("Directory.Build.targets")]
    [InlineData("../Directory.Build.props")]
    public async Task AddedSourceOrBuildConfigurationInvalidatesIndex(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(fixture.Root, relative));
        try
        {
            await File.WriteAllTextAsync(path, "// new input");
            var status = await IndexStore.CheckFreshnessAsync(fixture.Index);
            Assert.True(status.IsStale);
            Assert.Contains(relative, status.ChangedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RenamedAndDeletedSourceFilesAreDetected()
    {
        var original = Path.Combine(fixture.Root, "Caller", "Consumer.cs");
        var renamed = Path.Combine(fixture.Root, "Caller", "Renamed.cs");
        File.Move(original, renamed);
        try
        {
            var status = await IndexStore.CheckFreshnessAsync(fixture.Index);
            Assert.True(status.IsStale);
            Assert.Contains("Caller/Consumer.cs", status.ChangedFiles);
            Assert.Contains("Caller/Renamed.cs", status.ChangedFiles);
        }
        finally
        {
            File.Move(renamed, original);
        }
    }

    [Fact]
    public async Task BuildOutputsAndIndexFilesDoNotInvalidateSnapshot()
    {
        var bin = Directory.CreateDirectory(Path.Combine(fixture.Root, "Library", "bin"));
        var output = Path.Combine(bin.FullName, "Ignored.cs");
        var indexOutput = Path.Combine(fixture.Root, ".codeatlas", "Ignored.json");
        try
        {
            await File.WriteAllTextAsync(output, "generated output");
            await File.WriteAllTextAsync(indexOutput, "{}");
            Assert.False((await IndexStore.CheckFreshnessAsync(fixture.Index)).IsStale);
        }
        finally
        {
            File.Delete(output);
            File.Delete(indexOutput);
        }
    }

    [Fact]
    public async Task StaleSavePreservesPreviousArtifacts()
    {
        var previous = await File.ReadAllTextAsync(fixture.IndexPath);
        var overviewPath = Path.Combine(fixture.Root, ".codeatlas", "overview.md");
        var overview = await File.ReadAllTextAsync(overviewPath);
        var added = Path.Combine(fixture.Root, "Added.cs");
        try
        {
            await File.WriteAllTextAsync(added, "// changed while indexing");
            await Assert.ThrowsAsync<StaleIndexException>(() => IndexStore.SaveAsync(fixture.Index));
            Assert.Equal(previous, await File.ReadAllTextAsync(fixture.IndexPath));
            Assert.Equal(overview, await File.ReadAllTextAsync(overviewPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.IndexPath)!, "*.tmp"));
        }
        finally
        {
            File.Delete(added);
        }
    }

    [Fact]
    public async Task UnknownSchemaAndMalformedJsonAreRejected()
    {
        var path = Path.Combine(fixture.Root, ".codeatlas", "invalid.json");
        try
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.IndexPath))!;
            json["schemaVersion"] = 999;
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var unsupported = await Assert.ThrowsAsync<IndexFormatException>(() => IndexStore.LoadAsync(path));
            Assert.Equal("unsupported_index", unsupported.Code);
            await File.WriteAllTextAsync(path, "{");
            var malformed = await Assert.ThrowsAsync<IndexFormatException>(() => IndexStore.LoadAsync(path));
            Assert.Equal("invalid_index", malformed.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildRefreshesSymbolsAndReferencesAfterEdit()
    {
        var files = new[] { "Library/Calculator.cs", "Caller/Consumer.cs" }
            .Select(p => Path.Combine(fixture.Root, p)).ToArray();
        var original = files.ToDictionary(p => p, File.ReadAllText);
        try
        {
            foreach (var (path, source) in original)
                await File.WriteAllTextAsync(path, source.Replace("Calculate", "Evaluate"));
            Assert.True((await IndexStore.CheckFreshnessAsync(fixture.Index)).IsStale);
            var rebuilt = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);
            await IndexStore.SaveAsync(rebuilt);
            var saved = await IndexStore.LoadAsync(fixture.IndexPath);
            Assert.NotEqual(fixture.Index.IndexId, saved.IndexId);
            Assert.False((await IndexStore.CheckFreshnessAsync(saved)).IsStale);
            Assert.Empty(IndexStore.FindMethods(saved, "Calculate"));
            Assert.Equal(2, IndexStore.FindMethods(saved, "Evaluate").Count);
            Assert.Equal(3, IndexStore.FindReferences(saved, "Library/Library.csproj::M:Sample.Calculator.Evaluate(System.Int32)").Count);
        }
        finally
        {
            foreach (var (path, source) in original)
                await File.WriteAllTextAsync(path, source);
            await IndexStore.SaveAsync(fixture.Index);
        }
    }
}
