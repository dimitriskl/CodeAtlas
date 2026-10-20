using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

internal sealed class PhysicalRefreshFixture
{
    internal string Root { get; }
    internal string Solution => Path.Combine(Root, "Sample.slnx");
    internal string Calculator => Path.Combine(Root, "Library", "Calculator.cs");
    internal string Manifest => IndexStore.DefaultPath(Solution);
    internal CodebaseIndex Baseline { get; private set; } = null!;

    private PhysicalRefreshFixture(string root) => Root = root;

    internal static async Task<PhysicalRefreshFixture> CreateAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Required checkout not found.");
        var root = Path.Combine(repository.FullName, ".codeatlas", "validation", "m05-tests", Guid.NewGuid().ToString("N"));
        var fixture = new PhysicalRefreshFixture(root);
        Directory.CreateDirectory(root);
        foreach (var relative in new[] { "Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs",
                     "Library/packages.lock.json", "Caller/Caller.csproj", "Caller/Consumer.cs", "Caller/packages.lock.json" })
        {
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repository.FullName, "tests", "Fixtures", relative), target);
        }
        foreach (var name in new[] { "Directory.Build.props", "Version.props" })
            File.Copy(Path.Combine(repository.FullName, name), Path.Combine(root, name));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "restore", "Sample.slnx", "--locked-mode" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await output + await error);
        fixture.Baseline = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);
        await IndexStore.SaveAsync(fixture.Baseline);
        return fixture;
    }

    internal static void EqualAnalysis(CodebaseIndex expected, CodebaseIndex actual)
    {
        Assert.Equal(expected.Projects.Select(project => (project.Path, project.Name)), actual.Projects.Select(project => (project.Path, project.Name)));
        foreach (var (left, right) in expected.Projects.Zip(actual.Projects))
        {
            Assert.Equal(left.Dependencies, right.Dependencies);
            Assert.Equal(left.Files, right.Files);
        }
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Methods, actual.Methods);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Symbols), System.Text.Json.JsonSerializer.Serialize(actual.Symbols));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.DocumentationCoverage), System.Text.Json.JsonSerializer.Serialize(actual.DocumentationCoverage));
        Assert.Equal(expected.References.Keys, actual.References.Keys);
        foreach (var (id, references) in expected.References) Assert.Equal(references, actual.References[id]);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.InputManifest!.Coverage.Status, actual.InputManifest!.Coverage.Status);
        Assert.Equal(expected.InputManifest.Coverage.Scope, actual.InputManifest.Coverage.Scope);
        Assert.Equal(expected.InputManifest.Coverage.Issues, actual.InputManifest.Coverage.Issues);
        Assert.Equal(expected.Fingerprints.OrderBy(pair => pair.Key), actual.Fingerprints.OrderBy(pair => pair.Key));
        Assert.Equal(expected.InputManifest.Files.OrderBy(pair => pair.Key), actual.InputManifest.Files.OrderBy(pair => pair.Key));
        Assert.Equal(IncrementalDigest.Json(expected.InputManifest.Evidence), IncrementalDigest.Json(actual.InputManifest.Evidence));
        Assert.Equal(expected.InputManifest.EnvironmentDigest, actual.InputManifest.EnvironmentDigest);
    }
}

public sealed class IncrementalRefreshTests
{
    [Theory]
    [InlineData("Calculate(1)", "Calculate(\"edited\").Length")]
    [InlineData("=> value + 1;", "=>\n value + 7;")]
    public async Task BodyEditsReuseUnchangedOriginsAndEqualCleanFullAnalysis(string before, string after)
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var original = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, original.Replace(before, after, StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal("incremental-body", actual.Refresh!.Mode);
        Assert.True(actual.Refresh.ReusedReferences > 0);
        Assert.Equal(actual.Methods.Length, actual.Refresh.ReferenceSearches);
        Assert.InRange(actual.Refresh.ChangedDocumentContexts, 1, actual.Refresh.TotalDocumentContexts - 1);
        Assert.Equal(fixture.Baseline.InputManifest!.Coverage.Status, actual.InputManifest!.Coverage.Status);
        Assert.NotEqual(fixture.Baseline.IndexId, actual.IndexId);
    }

    [Fact]
    public async Task NoOpRefreshPublishesANewIdentityWithoutReferenceSearches()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        await IndexStore.SaveAsync(actual);

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(fixture.Baseline, actual);
        Assert.Equal("incremental-noop", actual.Refresh!.Mode);
        Assert.Equal(0, actual.Refresh.ReferenceSearches);
        Assert.Equal(0, actual.Refresh.ChangedDocumentContexts);
        Assert.Equal(fixture.Baseline.References.Values.Sum(references => references.LongLength), actual.Refresh.ReusedReferences);
        Assert.NotEqual(fixture.Baseline.IndexId, actual.IndexId);
        Assert.Equal(actual.IndexId, (await IndexStore.LoadSnapshotAsync(fixture.Manifest)).Index.IndexId);
    }

    [Fact]
    public async Task ADefaultArgumentChangeFallsBackEvenWhenTheDocumentationMethodIdIsUnchanged()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var source = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, source.Replace("int value)", "int value = 0)", StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal("full", actual.Refresh!.Mode);
        Assert.Equal("declaration_surface_changed", actual.Refresh.FallbackReason);
        Assert.Equal(0, actual.Refresh.ReusedReferences);
    }

    [Fact]
    public async Task ASecondRefreshUsesTheImmediatelyPreviousPublishedGeneration()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var source = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, source.Replace("Calculate(1)", "Calculate(2) + Calculate(3)", StringComparison.Ordinal));
        var first = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        await IndexStore.SaveAsync(first);
        await File.WriteAllTextAsync(fixture.Calculator, source.Replace("Calculate(1)", "Calculate(\"second\").Length", StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        var second = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, second);
        Assert.Equal(first.IndexId, second.Refresh!.BaselineIndexId);
        Assert.Equal("incremental-body", second.Refresh.Mode);
    }

    [Fact]
    public async Task LegacyIncrementalMetadataIsSeededByAFullRefresh()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var legacy = fixture.Baseline with { IndexId = Guid.NewGuid().ToString("N"), IncrementalState = null };
        await IndexStore.SaveAsync(legacy);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);

        // Assert
        Assert.Equal("full", actual.Refresh!.Mode);
        Assert.Equal("baseline_metadata_missing", actual.Refresh.FallbackReason);
        Assert.NotNull(actual.IncrementalState);
        Assert.Equal(actual.IndexId, actual.IncrementalState.IndexId);
    }

    [Fact]
    public async Task CancellationDuringRefreshPreservesThePublishedGenerationBytes()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.Manifest);
        var source = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, source.Replace("Calculate(1)", "Calculate(2)", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(item => { if (item.Stage == IndexStage.FindingReferences) cancellation.Cancel(); });

        // Act
        var failure = await Record.ExceptionAsync(() => SolutionAnalysis.RefreshIndexAsync(fixture.Solution, cancellation.Token, progress));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Equal(fixture.Baseline.IndexId, (await IndexStore.LoadSnapshotAsync(fixture.Manifest)).Index.IndexId);
    }

    private sealed class ImmediateProgress(Action<IndexProgress> action) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => action(value);
    }
}
