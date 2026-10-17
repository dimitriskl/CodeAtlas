using Xunit;

namespace CodeAtlas.Tests;

public sealed class SampleSolution : IAsyncLifetime
{
    public SolutionAnalysis Analysis { get; private set; } = null!;
    public string Root { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAtlas.slnx")))
            directory = directory.Parent;
        Root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        Analysis = await SolutionAnalysis.LoadAsync(Path.Combine(Root, "tests/Fixtures/Sample.slnx"));
    }

    public Task DisposeAsync()
    {
        Analysis.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class AnalysisTests(SampleSolution sample) : IClassFixture<SampleSolution>
{
    [Fact]
    public async Task OverloadsHaveDistinctIdsAndDeclarationLocations()
    {
        var methods = await sample.Analysis.FindMethodsAsync("Calculate");
        Assert.Equal(2, methods.Count);
        Assert.Contains(methods, m => m.Id == "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)");
        Assert.Contains(methods, m => m.Id == "Library/Library.csproj::M:Sample.Calculator.Calculate(System.String)");
        Assert.All(methods, m =>
        {
            Assert.Equal("Library/Calculator.cs", m.Location.File);
            Assert.Equal(m.Id.EndsWith("(System.Int32)", StringComparison.Ordinal) ? 23 : 26, m.Location.Column);
        });
        Assert.Equal(new[] { 5, 6 }, methods.Select(m => m.Location.Line).Order());
    }

    [Fact]
    public async Task IntegerOverloadFindsLocalAndCrossProjectCallsAndMethodGroup()
    {
        const string id = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)";
        var references = await sample.Analysis.FindReferencesAsync(id);
        Assert.Equal(3, references.Count);
        Assert.All(references, r => Assert.Equal(id, r.DefinitionId));
        Assert.Collection(references,
            r => { Assert.Equal("Caller/Consumer.cs", r.Location.File); Assert.Equal(7, r.Location.Line); Assert.Equal("invocation", r.Kind); },
            r => { Assert.Equal("Caller/Consumer.cs", r.Location.File); Assert.Equal(9, r.Location.Line); Assert.Equal("reference", r.Kind); },
            r => { Assert.Equal("Library/Calculator.cs", r.Location.File); Assert.Equal(7, r.Location.Line); Assert.Equal("invocation", r.Kind); });
        foreach (var reference in references)
        {
            var source = File.ReadAllLines(Path.Combine(sample.Root, "tests/Fixtures", reference.Location.File));
            Assert.StartsWith("Calculate", source[reference.Location.Line - 1][(reference.Location.Column - 1)..]);
        }
    }

    [Fact]
    public async Task StringOverloadDoesNotIncludeIntegerCalls()
    {
        var reference = Assert.Single(await sample.Analysis.FindReferencesAsync("Library/Library.csproj::M:Sample.Calculator.Calculate(System.String)"));
        Assert.Equal("Caller/Consumer.cs", reference.Location.File);
        Assert.Equal(8, reference.Location.Line);
        Assert.Equal("invocation", reference.Kind);
    }

    [Fact]
    public async Task UnknownNameReturnsNoCandidates()
    {
        Assert.Empty(await sample.Analysis.FindMethodsAsync("MissingMethod"));
    }

    [Theory]
    [InlineData("Calculate")]
    [InlineData("Missing.csproj::M:Sample.Calculator.Calculate(System.Int32)")]
    [InlineData("Library/Library.csproj::M:Sample.Calculator.Missing")]
    public async Task InvalidOrStaleIdsFailExplicitly(string id)
    {
        await Assert.ThrowsAsync<AnalysisException>(() => sample.Analysis.FindReferencesAsync(id));
    }

    [Fact]
    public async Task MissingSolutionFailsExplicitly()
    {
        await Assert.ThrowsAsync<AnalysisException>(() => SolutionAnalysis.LoadAsync(Path.Combine(sample.Root, "missing.slnx")));
    }

    [Fact]
    public async Task UnloadableProjectDoesNotProducePartialResults()
    {
        var directory = Directory.CreateTempSubdirectory("codeatlas-invalid-");
        try
        {
            var path = Path.Combine(directory.FullName, "Invalid.slnx");
            await File.WriteAllTextAsync(path, "<Solution><Project Path=\"Missing.csproj\" /></Solution>");
            var exception = await Assert.ThrowsAsync<AnalysisException>(() => SolutionAnalysis.LoadAsync(path));
            Assert.NotEmpty(exception.Diagnostics);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
