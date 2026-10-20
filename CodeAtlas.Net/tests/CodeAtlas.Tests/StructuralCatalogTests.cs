using Xunit;

namespace CodeAtlas.Tests;

// DRAFT ONLY; uses the M6 in-memory DocumentationFixture. Never reads eConnector/evaluation data.
public sealed class StructuralCatalogTests
{
    [Fact]
    public async Task ResolvedOverloadsAndInterfaceImplementationsPreserveSourceIdentityAndEvidence()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Facts.cs", """
            namespace Facts;
            public interface IConvert<T> { T Convert(T value); }
            public class Service : IConvert<int>
            {
                public int Convert(int value) => value;
                public int Convert(string value) => value.Length;
                public int Run() => Convert(1);
                public void Container()
                {
                    int Local() { return Convert(2); }
                    System.Func<int> delayed = () => Convert(3);
                    Convert(4);
                }
            }
            """));
        var symbols = await fixture.ExtractAsync();
        var convert = symbols.Single(s => s.ContainingType == "Facts.Service" && s.Id.EndsWith("Convert(System.Int32)", StringComparison.Ordinal));
        var run = symbols.Single(s => s.Name == "Run");
        var container = symbols.Single(s => s.Name == "Container");

        // Act
        var catalog = await StructuralCatalogBuilder.CreateAsync(fixture.Solution, fixture.SolutionPath, symbols);

        // Assert
        var call = Assert.Single(catalog.Facts, f => f.OwnerId == run.Id && f.Kind == "resolved-invocation");
        Assert.Equal(convert.Id, call.Target.SourceSymbolId);
        Assert.Equal("indexed-source", call.Target.Resolution);
        Assert.Equal("explicit-source-binding", call.Evidence.SemanticScope);
        Assert.Equal("roslyn", call.Evidence.Origin);
        Assert.Single(catalog.Facts, f => f.OwnerId == container.Id && f.Kind == "resolved-invocation");
        var implementation = Assert.Single(catalog.Facts, f => f.OwnerId == convert.Id && f.Kind == "implements-member");
        Assert.Equal(symbols.Single(s => s.ContainingType == "Facts.IConvert<T>" && s.Name == "Convert").Id,
            implementation.Target.SourceSymbolId);
        Assert.Contains(catalog.Facts, f => f.Kind == "direct-interface" && f.Target.Signature == "Facts.IConvert<int>");
        Assert.Contains(catalog.Coverage.Exclusions, e => e == "anonymous-function-bodies");
        Assert.False(catalog.Coverage.RuntimeCallGraph);
        Assert.All(catalog.Facts, f => Assert.Contains(symbols, s => s.Id == f.OwnerId));
        Assert.True(call.Evidence.Location.Start.Line > 0);
        Assert.True(call.Evidence.Location.End.Line >= call.Evidence.Location.Start.Line);
    }

    [Fact]
    public async Task CrossProjectBindingsAndLinkedDeclarationsKeepTheirActualProjectIdentities()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(
            ("Library", "Work.cs", "namespace Library; public class Work { public static int Run() => 1; }"),
            ("Client", "Worker.cs", "namespace Client; public class Worker { public int Go() => Library.Work.Run(); }"));
        fixture.AddProjectReference("Client", "Library");
        fixture.AddLinkedDocumentToBoth("Shared.cs", "namespace Shared; public class Link { public int Value; }");
        var symbols = await fixture.ExtractAsync();
        var owner = symbols.Single(s => s.Name == "Go");
        var target = symbols.Single(s => s.Name == "Run");
        var linkedFields = symbols.Where(s => s.Name == "Value").ToArray();

        // Act
        var catalog = await StructuralCatalogBuilder.CreateAsync(fixture.Solution, fixture.SolutionPath, symbols);

        // Assert
        Assert.Equal(target.Id, Assert.Single(catalog.Facts, f => f.OwnerId == owner.Id && f.Kind == "resolved-invocation").Target.SourceSymbolId);
        Assert.Equal(2, linkedFields.Length);
        Assert.Equal(2, linkedFields.Select(s => s.Project).Distinct().Count());
        Assert.All(linkedFields, s => Assert.Single(catalog.Facts,
            f => f.OwnerId == s.Id && f.Kind == "containing-type" && f.Target.SourceSymbolId!.StartsWith(s.Project + "::", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CurrentSourceEditsRefreshBindingAndEvidenceWithoutRewritingOldCatalog()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Edit.cs", """
            namespace Edits;
            public class Worker
            {
                public int Select(int value) => value;
                public int Select(string value) => value.Length;
                public int Go() => Select(1);
            }
            """));
        var symbols = await fixture.ExtractAsync();
        var before = await StructuralCatalogBuilder.CreateAsync(fixture.Solution, fixture.SolutionPath, symbols);
        var document = fixture.Solution.Projects.Single().Documents.Single();
        var text = (await document.GetTextAsync()).ToString();
        fixture.Solution = fixture.Solution.WithDocumentText(document.Id,
            Microsoft.CodeAnalysis.Text.SourceText.From("\n\n" + text.Replace("Select(1)", "Select(\"x\")", StringComparison.Ordinal)));
        var currentSymbols = await fixture.ExtractAsync();

        // Act
        var after = await StructuralCatalogBuilder.CreateAsync(fixture.Solution, fixture.SolutionPath, currentSymbols);

        // Assert
        var oldCall = Assert.Single(before.Facts, f => f.Kind == "resolved-invocation");
        var newCall = Assert.Single(after.Facts, f => f.Kind == "resolved-invocation");
        Assert.EndsWith("Select(System.Int32)", oldCall.Target.SourceSymbolId);
        Assert.EndsWith("Select(System.String)", newCall.Target.SourceSymbolId);
        Assert.Equal(oldCall.Evidence.Location.Start.Line + 2, newCall.Evidence.Location.Start.Line);
    }
}
