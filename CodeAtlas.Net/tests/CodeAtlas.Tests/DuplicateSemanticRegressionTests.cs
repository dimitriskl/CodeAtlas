using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateSemanticRegressionTests
{
    [Fact]
    public async Task LiteralContentsAndBoundMemberTargetsAreNotExactDuplicates()
    {
        // Arrange
        using var fixture = new SemanticFixture("""
            public static class Cases {
              static string Save(string value) => value;
              static string Delete(string value) => value;
              public static string A(string value) { string text = "a\\b"; if(value.Length > 0) return Save(text); return value; }
              public static string B(string value) { string text = "a  b"; if(value.Length > 0) return Save(text); return value; }
              public static string C(string value) { string text = "a\\b"; if(value.Length > 0) return Delete(text); return value; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.DoesNotContain(catalog.Groups, group => group.Reason == "exact-normalized-body");
    }

    [Fact]
    public async Task ParameterAndLocalReferencesPreserveTheirActualOrdinals()
    {
        // Arrange
        using var fixture = new SemanticFixture("""
            public static class Cases {
              public static int A(int first, int second) { int a=first+1; int b=second+2; if(first>0) return a-b; return first; }
              public static int B(int first, int second) { int a=first+1; int b=second+2; if(first>0) return b-a; return second; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.DoesNotContain(catalog.Groups, group => group.Reason == "exact-normalized-body");
    }

    [Fact]
    public async Task NearPairsInMoreThanThirtyOrdinaryBlockBodiesAreCompared()
    {
        // Arrange
        var methods = string.Join("\n", Enumerable.Range(0,31).Select(i =>
          $"public static int M{i}(int value) {{ int total=value+{i}; int scaled=total*3; if(total>10) return scaled; return value; }}"));
        using var fixture = new SemanticFixture("public static class Cases {" + methods + "}");

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.NotNull(catalog.Analysis);
        Assert.Equal(31,catalog.Analysis.Bodies.Length);
        Assert.Empty(catalog.Analysis.Omissions);
        Assert.All(catalog.Analysis.Bodies,b => Assert.True(b.Tokens.Length >= catalog.Analysis.Limits.MinimumTokens));
        Assert.NotNull(catalog.Scan);
        Assert.True(catalog.Scan.Work.ComparedPairs > 0);
        Assert.Contains(catalog.Groups, group => group.Reason == "near-normalized-body");
    }

    [Fact]
    public async Task ImplicitCallerConstantsRemainPartOfTheNormalizedBody()
    {
        // Arrange
        using var fixture = new SemanticFixture("""
            using System.Runtime.CompilerServices;
            public static class Cases {
              static string Target([CallerMemberName] string caller = "") => caller;
              public static string A(int value) { string text=Target(); if(value>2) return text.Trim(); return text.ToUpperInvariant(); }
              public static string B(int value) { string text=Target(); if(value>2) return text.Trim(); return text.ToUpperInvariant(); }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.DoesNotContain(catalog.Groups, group => group.Reason == "exact-normalized-body");
    }
    internal sealed class SemanticFixture : IDisposable
    {
        private readonly AdhocWorkspace workspace = new();
        internal Solution Solution { get; }
        internal string SolutionPath { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-m10-public", "Sample.slnx");
        internal SemanticFixture(string source)
        {
            var directory = Path.GetDirectoryName(SolutionPath)!;
            var projectId = ProjectId.CreateNewId();
            var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
              ?? throw new InvalidOperationException("Trusted platform assemblies unavailable."))
              .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
            var info = ProjectInfo.Create(projectId, VersionStamp.Create(), "Library", "Library", LanguageNames.CSharp,
              filePath: Path.Combine(directory,"Library","Library.csproj"),
              parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
              compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references);
            Solution = workspace.CurrentSolution.AddProject(info).AddDocument(DocumentId.CreateNewId(projectId),
              "Cases.cs", SourceText.From(source,Encoding.UTF8), filePath: Path.Combine(directory,"Library","Cases.cs"));
        }
        internal Task<DuplicateCatalog> ExtractAsync() => DuplicateFinder.CreateAsync(Solution,SolutionPath);
        public void Dispose() => workspace.Dispose();
    }
}


