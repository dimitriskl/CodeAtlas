using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

// Semantic invariants; these fixtures do not establish usefulness or production performance.
public sealed class DuplicateSemanticTests
{
    [Fact]
    public async Task SiblingScopesWithTheSameLocalSpellingRetainDistinctBoundSlots()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int A(int input) { int result=0; { int value=input+1; result=value; } { int value=input+2; result+=value; } return result; }
                public static int B(int input) { int result=0; { int first=input+1; result=first; } { int second=input+2; result+=second; } return result; }
                public static int C(int input) { int result=0; int first=input+1; int second=input+2; result=first; result+=first; return result; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.Equal(catalog.Bodies[0].NormalizedHash,catalog.Bodies[1].NormalizedHash);
        Assert.NotEqual(catalog.Bodies[0].NormalizedHash,catalog.Bodies[2].NormalizedHash);
        Assert.Equal(new[] { "P0", "L0", "L1", "L2" },catalog.Bodies[0].Names.Select(n => n.Slot));
    }

    [Fact]
    public async Task OnlyResolvedParameterAndLocalSlotsAreRenamed()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int A(int value) { int total = value + 2; int scaled = total * 3; if (total > 10) return scaled; return value; }
                public static int B(int input) { int result = input + 2; int multiplied = result * 3; if (result > 10) return multiplied; return input; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();
        var scan = DuplicateDetector.Scan(catalog, "snapshot-a");

        // Assert
        Assert.Empty(catalog.Omissions);
        Assert.All(catalog.Bodies,b => Assert.True(b.Tokens.Length >= catalog.Limits.MinimumTokens));
        var finding = Assert.Single(scan.Candidates);
        Assert.Equal("renamed-structure", finding.Kind);
        Assert.Equal(1, finding.Score);
        Assert.Contains(finding.ImportantDifferences, d => d.Kind == "slot-name-or-type");
        Assert.Equal(catalog.Bodies[0].NormalizedHash, catalog.Bodies[1].NormalizedHash);
        Assert.NotEqual(catalog.Bodies[0].LexicalHash, catalog.Bodies[1].LexicalHash);
        Assert.All(catalog.Bodies, b => Assert.Equal(b.Tokens.Length, b.TokenOffsets.Length));
    }

    [Fact]
    public async Task ConstantsOperatorsMemberBindingsAndParameterOrdinalsRemainSignificant()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int A(int first, int second) { int result = first + second; if (result > 10) return result * 3; return first; }
                public static int B(int first, int second) { int result = first + second; if (result > 11) return result * 3; return first; }
                public static int C(int first, int second) { int result = first + second; if (result < 10) return result * 3; return first; }
                public static int D(int first, int second) { int result = first + second; if (result > 10) return result * 3; return second; }
                public static string E(string value) { string result = value.Trim(); if (result.Length > 10) return result.ToUpperInvariant(); return value; }
                public static string F(string value) { string result = value.Trim(); if (result.Length > 10) return result.ToLowerInvariant(); return value; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.Equal(6, catalog.Bodies.Select(b => b.NormalizedHash).Distinct().Count());
        Assert.Empty(catalog.Omissions);
        Assert.Contains(catalog.Vocabulary, key => key.Contains("ToUpperInvariant", StringComparison.Ordinal));
        Assert.Contains(catalog.Vocabulary, key => key.Contains("ToLowerInvariant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameSpellingOfDifferentLocalsDoesNotCollapseResolvedSlots()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int A(int value) { int first = value + 1; int second = value + 2; if (value > 0) return first - second; return value; }
                public static int B(int value) { int left = value + 1; int right = value + 2; if (value > 0) return right - left; return value; }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();

        // Assert
        Assert.NotEqual(catalog.Bodies[0].NormalizedHash, catalog.Bodies[1].NormalizedHash);
        Assert.All(catalog.Bodies, b => Assert.Equal(new[] { "P0", "L0", "L1" }, b.Names.Select(n => n.Slot)));
    }

    [Fact]
    public async Task CallerMemberNameDefaultValueIsNotErasedByMethodNameNormalization()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            using System.Runtime.CompilerServices;
            public static class Cases {
                public static string Target([CallerMemberName] string caller = "") => caller;
                public static string A(int value) { string text = Target(); if (value > 2) return text.Trim(); return text.ToUpperInvariant(); }
                public static string B(int value) { string text = Target(); if (value > 2) return text.Trim(); return text.ToUpperInvariant(); }
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();
        var a = catalog.Bodies.Single(b => b.SymbolId.Contains(".A(", StringComparison.Ordinal));
        var b = catalog.Bodies.Single(b => b.SymbolId.Contains(".B(", StringComparison.Ordinal));

        // Assert
        Assert.NotEqual(a.NormalizedHash, b.NormalizedHash);
        Assert.Contains(catalog.Vocabulary, key => key.Contains("ImplicitArguments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedBodiesHaveExplicitOmissionsAndShortWrappersAreFilteredOnlyFromDetection()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int ShortA(int value) => value;
                public static int ShortB(int value) => value;
                public static int Local(int value) { int Nested() => value + 1; return Nested(); }
                public static int Lambda(int value) { System.Func<int> work = () => value; return work(); }
                public static T Generic<T>(T value) => value;
                public static int Pattern(object value) => value is int number ? number : 0;
                public static string Name(int value) => nameof(value);
            }
            """);

        // Act
        var catalog = await fixture.ExtractAsync();
        var scan = DuplicateDetector.Scan(catalog, "snapshot-a");

        // Assert
        Assert.Equal(2, catalog.Bodies.Length);
        Assert.Equal(5, catalog.Omissions.Length);
        Assert.Equal(2, scan.Work.FilteredTrivialMethods);
        Assert.Empty(scan.Candidates);
        Assert.False(catalog.Coverage.CompleteForRealizedContexts);
    }

    [Fact]
    public async Task LargeExactGroupsAreSuppressedAndPairWorkIsExplicitlyBounded()
    {
        // Arrange
        var methods = string.Join("\n", Enumerable.Range(0, 80).Select(i =>
            $"public static int M{i}(int value) {{ int total = value + 2; int scaled = total * 3; if (total > 10) return scaled; return value; }}"));
        using var fixture = Fixture.Create("public static class Cases {" + methods + "}");
        var limits = new DuplicateLimits(MaximumPosting: 4, MaximumPairVisits: 10, MaximumComparisons: 5);
        var catalog = await fixture.ExtractAsync(limits);

        // Act
        var scan = DuplicateDetector.Scan(catalog, "snapshot-a");

        // Assert
        Assert.True(scan.Work.WorkCapped);
        Assert.Contains("posting-limit", scan.Work.CapReasons);
        Assert.True(scan.Work.PairVisits <= 10);
        Assert.True(scan.Work.GeneratedPairs <= 5);
        Assert.Empty(scan.Candidates);
    }

    [Fact]
    public async Task ReextractingCurrentBodyChangesEvidenceEvenWithTheSameMethodIdentity()
    {
        // Arrange
        using var before = Fixture.Create("public static class Cases { public static int A(int value) { int x=value+2; if(x>10) return x*3; return value; } }");
        using var after = Fixture.Create("public static class Cases { public static int A(int value) { int x=value+2; if(x>11) return x*3; return value; } }");
        var oldCatalog = await before.ExtractAsync();

        // Act
        var newCatalog = await after.ExtractAsync();

        // Assert
        Assert.Equal(oldCatalog.Bodies[0].SymbolId, newCatalog.Bodies[0].SymbolId);
        Assert.NotEqual(oldCatalog.Bodies[0].FileHash, newCatalog.Bodies[0].FileHash);
        Assert.NotEqual(oldCatalog.Bodies[0].NormalizedHash, newCatalog.Bodies[0].NormalizedHash);
    }

    [Fact]
    public async Task MatchingDeveloperSummariesRemainPurposeCandidatesEvenWhenLogicDiffers()
    {
        // Arrange
        var noise = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"public static int Noise{i}() => {i};"));
        using var fixture = Fixture.Create("""
            public static class Cases {
                /// <summary>Check whether the debit is permitted against the balance.</summary>
                public static bool CanDebitA(int balance, int debit) { if (debit > balance) return false; return debit >= 0; }
                /// <summary>Check whether the debit is permitted against the balance.</summary>
                public static bool CanDebitB(int balance, int debit) { return balance < 0 || debit == 0; }
            """ + noise + "}");
        var symbols = await fixture.ExtractDocumentationAsync();
        var catalog = await fixture.ExtractAsync();
        var scan = DuplicateDetector.Scan(catalog, "snapshot-a");
        var selected = symbols.Single(s => s.Name == "CanDebitA");
        var facts = new StructuralCatalog(StructuralCoverage.Current, [], []);

        // Act
        var result = PurposeOverlap.Build(symbols, facts, new DuplicateCatalog(DuplicateCatalog.CurrentVersion, []) { Analysis=catalog, Scan=scan }, "snapshot-a", selected.Id, new QueryRequest());

        // Assert
        var candidate = Assert.Single(result.Page.Items, c => c.Right.SymbolId.Contains(".CanDebitB(", StringComparison.Ordinal));
        Assert.Equal("similar-purpose-candidate", candidate.Kind);
        Assert.Contains(candidate.Caveats, text => text.Contains("different business rules", StringComparison.Ordinal));
        Assert.NotEmpty(result.SelectedDocumentationEvidence);
        Assert.Contains(candidate.Reasons, reason => reason.DocumentationEvidence is not null);
    }

    [Fact]
    public async Task FindingsAndPublicAuditAreDeterministicForOneCatalog()
    {
        // Arrange
        using var fixture = Fixture.Create("""
            public static class Cases {
                public static int A(int value) { int total=value+2; int scaled=total*3; if(total>10) return scaled; return value; }
                public static int B(int value) { int total=value+2; int scaled=total*3; if(total>10) return scaled; return value; }
            }
            """);
        var catalog = await fixture.ExtractAsync();
        var firstTrace = new List<DuplicatePairTrace>();
        var secondTrace = new List<DuplicatePairTrace>();

        // Act
        var first = DuplicateDetector.Scan(catalog, "snapshot-a", trace: firstTrace.Add);
        var second = DuplicateDetector.Scan(catalog, "snapshot-a", trace: secondTrace.Add);

        // Assert
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.Equal(firstTrace, secondTrace);
        Assert.Equal("exact-structure", Assert.Single(first.Candidates).Kind);
        Assert.All(first.Candidates, c => Assert.Contains(c.Caveats, s => s.Contains("no behavioral-equivalence", StringComparison.Ordinal)));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly AdhocWorkspace workspace = new();
        private readonly Solution solution;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "codeatlas-m10-public");

        private Fixture(string source)
        {
            var projectId = ProjectId.CreateNewId();
            var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                ?? throw new InvalidOperationException("Trusted platform assembly inventory is unavailable."))
                .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
            var info = ProjectInfo.Create(projectId, VersionStamp.Create(), "Library", "Library", LanguageNames.CSharp,
                filePath: Path.Combine(directory, "Library", "Library.csproj"),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references);
            solution = workspace.CurrentSolution.AddProject(info).AddDocument(DocumentId.CreateNewId(projectId),
                "Cases.cs", SourceText.From(source, Encoding.UTF8), filePath: Path.Combine(directory, "Library", "Cases.cs"));
        }

        internal static Fixture Create(string source) => new(source);

        internal Task<DocumentedSymbol[]> ExtractDocumentationAsync() =>
            DocumentationCatalog.CreateAsync(solution, Path.Combine(directory, "Sample.slnx"));

        internal async Task<DuplicateBodyCatalog> ExtractAsync(DuplicateLimits? limits = null)
        {
            var compilation = await solution.Projects.Single().GetCompilationAsync();
            var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors.Select(e => e.ToString())));
            return await DuplicateCatalogBuilder.CreateAsync(solution, Path.Combine(directory, "Sample.slnx"),
                new string('A', 64), new Dictionary<string, string> { ["Library/Library.csproj"] = new string('B', 64) }, limits);
        }

        public void Dispose() => workspace.Dispose();
    }
}




