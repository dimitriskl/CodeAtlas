using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SemanticSolution : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    public CodebaseIndex Index { get; private set; } = null!;
    public SolutionAnalysis Analysis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        Root = Path.Combine(repository!.FullName, "tests", "SemanticFixtures");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "restore", "Semantic.slnx", "--locked-mode" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        Index = await SolutionAnalysis.CreateIndexAsync(Path.Combine(Root, "Semantic.slnx"));
        Analysis = await SolutionAnalysis.LoadAsync(Index.Solution);
    }

    public Task DisposeAsync() { Analysis.Dispose(); return Task.CompletedTask; }
}

public sealed class SemanticTests(SemanticSolution fixture) : IClassFixture<SemanticSolution>
{
    private const string Prefix = "Library/Library.csproj::M:Semantic.";

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task WorkerCountsPreserveEveryOrderedReferenceAndMethodKey(int workers)
    {
        // Arrange
        var expected = fixture.Index;

        // Act
        var actual = await SolutionAnalysis.CreateIndexAsync(expected.Solution, referenceWorkers: workers);

        // Assert
        Assert.Equal(expected.Methods, actual.Methods);
        Assert.Equal(expected.References.Keys, actual.References.Keys);
        foreach (var (id, references) in expected.References)
            Assert.Equal(references, actual.References[id]);
    }

    [Fact]
    public async Task IndependentAnalysisHasExactlyTheSameOrderedReferencesForEveryMethod()
    {
        // Arrange
        var expected = fixture.Index.References;

        // Act
        var actual = new Dictionary<string, SourceReference[]>();
        foreach (var method in fixture.Index.Methods)
            actual[method.Id] = (await fixture.Analysis.FindReferencesAsync(method.Id)).ToArray();

        // Assert
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (id, references) in expected) Assert.Equal(references, actual[id]);
    }

    [Theory]
    [InlineData("Algorithms.Transform(System.Int32)", "Transform", 9, "invocation")]
    [InlineData("Algorithms.Transform(System.String)", "Transform", 10, "invocation")]
    [InlineData("Algorithms.Partial(System.Int32)", "Partial", 14, "invocation")]
    public async Task OverloadsAndPartialMethodsKeepTheirOwnSourceBackedCalls(string declaration, string token, int line, string kind)
    {
        // Arrange
        var id = Prefix + declaration;
        var expectedCount = declaration.Contains("Int32") && token == "Transform" ? 3 : 1;

        // Act
        var references = await fixture.Analysis.FindReferencesAsync(id);

        // Assert
        Assert.Equal(expectedCount, references.Count);
        Assert.Contains(references, reference => reference.Location.Line == line && reference.Kind == kind);
        Assert.All(references, reference => Assert.Equal(id, reference.DefinitionId));
        AssertSourceTokens(references, token);
        if (token == "Partial")
            Assert.Single(fixture.Index.Methods, method => method.Id == id);
        if (token == "Transform" && expectedCount == 3)
        {
            Assert.Contains(references, reference => reference.Location.Line == 6 && reference.Kind == "reference");
            Assert.Contains(references, reference => reference.Location.Line == 11 && reference.Kind == "reference");
        }
    }

    [Fact]
    public async Task ConstructedGenericReferencesPreserveLinkedFileProjectIdentities()
    {
        // Arrange
        const string id = Prefix + "Algorithms.Identity``1(``0)";

        // Act
        var references = await fixture.Analysis.FindReferencesAsync(id);

        // Assert
        Assert.Equal(5, references.Count);
        var linked = references.Where(reference => reference.Location.File == "SharedCalls.cs").ToArray();
        Assert.Equal(new[] { "Client/Client.csproj", "Library/Library.csproj" }, linked.Select(reference => reference.Project));
        Assert.All(linked, reference => Assert.Equal("invocation", reference.Kind));
        Assert.Contains(references, reference => reference.Location.Line == 6 && reference.Kind == "reference");
        Assert.All(references, reference => Assert.Equal(id, reference.DefinitionId));
        AssertSourceTokens(references, "Identity");
    }

    [Theory]
    [InlineData("IWorker.Work(System.Int32)")]
    [InlineData("Worker.Work(System.Int32)")]
    [InlineData("DerivedWorker.Work(System.Int32)")]
    [InlineData("ExplicitWorker.Semantic#IWorker#Work(System.Int32)")]
    public async Task RelatedInterfaceAndOverrideDefinitionsAreNotRelabeledAsTheSelectedMethod(string declaration)
    {
        // Arrange
        var id = Prefix + declaration;

        // Act
        var references = await fixture.Analysis.FindReferencesAsync(id);

        // Assert
        Assert.Equal(4, references.Count);
        Assert.Equal(new[] { 16, 17, 18, 20 }, references.Select(reference => reference.Location.Line));
        Assert.Equal(new[] { Prefix + "IWorker.Work(System.Int32)", Prefix + "Worker.Work(System.Int32)",
            Prefix + "DerivedWorker.Work(System.Int32)", Prefix + "IWorker.Work(System.Int32)" },
            references.Select(reference => reference.DefinitionId));
        Assert.All(references, reference => Assert.Equal("invocation", reference.Kind));
        AssertSourceTokens(references, "Work");
    }

    [Fact]
    public void InheritedInterfaceImplementationKeepsTheSharedReferenceSet()
    {
        // Arrange
        var alpha = fixture.Index.Methods.Single(method => method.Id.EndsWith("IAlpha.GetStream", StringComparison.Ordinal));
        var beta = fixture.Index.Methods.Single(method => method.Id.EndsWith("IBeta.GetStream", StringComparison.Ordinal));
        var runner = fixture.Index.Methods.Single(method => method.Id.EndsWith("BetaRunner.GetStream", StringComparison.Ordinal));
        var gamma = fixture.Index.Methods.Single(method => method.Id.EndsWith("IGamma.GetStream", StringComparison.Ordinal));
        var gammaRunner = fixture.Index.Methods.Single(method => method.Id.EndsWith("GammaRunner.GetStream", StringComparison.Ordinal));

        // Act
        var alphaReferences = fixture.Index.References[alpha.Id];
        var betaReferences = fixture.Index.References[beta.Id];
        var runnerReferences = fixture.Index.References[runner.Id];
        var gammaReferences = fixture.Index.References[gamma.Id];
        var gammaRunnerReferences = fixture.Index.References[gammaRunner.Id];

        // Assert
        Assert.Equal(betaReferences, alphaReferences);
        Assert.Equal(runnerReferences, alphaReferences);
        Assert.Equal([9, 10, 11, 12, 13], alphaReferences.Select(reference => reference.Location.Line));
        Assert.Equal(gammaReferences, gammaRunnerReferences);
        Assert.Equal([14, 15], gammaReferences.Select(reference => reference.Location.Line));
        Assert.DoesNotContain(alphaReferences, reference => reference.DefinitionId == gamma.Id);
    }

    private void AssertSourceTokens(IEnumerable<SourceReference> references, string token)
    {
        foreach (var reference in references)
        {
            var line = File.ReadAllLines(Path.Combine(fixture.Root, reference.Location.File))[reference.Location.Line - 1];
            Assert.StartsWith(token, line[(reference.Location.Column - 1)..]);
        }
    }
}
