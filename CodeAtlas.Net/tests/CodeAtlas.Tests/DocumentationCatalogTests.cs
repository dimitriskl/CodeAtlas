using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DocumentationCatalogTests
{
    private const string Prefix = "Library/Library.csproj::";

    [Fact]
    public async Task GenericNestedTypesOverloadsPropertiesAndSharedFieldsKeepRoslynIdentityAndAuthoredText()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Symbols.cs", """
            namespace Docs;
            /// <summary>Outer author wording.</summary>
            /// <typeparam name="T">An outer item.</typeparam>
            public class Outer<T>
            {
                /// <summary>Nested author wording.</summary>
                public class Inner<U>
                {
                    /// <summary>Two fields share this declaration.</summary>
                    public int First, Second;
                    /// <summary>Current tally.</summary><value>The stored tally.</value>
                    public int Count { get; set; }
                    /// <summary>Add the integer.</summary><param name="value">Original integer wording.</param>
                    /// <returns>The computed integer.</returns><remarks>Keep this punctuation: A &amp; B.</remarks>
                    public int Calculate(int value) => value;
                    /// <summary>Keep the string.</summary><param name="value">Original string wording.</param>
                    public string Calculate(string value) => value;
                }
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var integer = symbols.Single(s => s.Id == Prefix + "M:Docs.Outer`1.Inner`1.Calculate(System.Int32)");
        var text = symbols.Single(s => s.Id == Prefix + "M:Docs.Outer`1.Inner`1.Calculate(System.String)");
        var nested = symbols.Single(s => s.Id == Prefix + "T:Docs.Outer`1.Inner`1");
        var fields = symbols.Where(s => s.Kind == "field").ToArray();

        // Assert
        Assert.Equal("Add the integer.", Section(integer, "summary").Text);
        Assert.Equal("Keep the string.", Section(text, "summary").Text);
        Assert.Equal("Original integer wording.", Section(integer, "param").Text);
        Assert.Equal("value", Section(integer, "param").AppliesToName);
        Assert.Equal("The computed integer.", Section(integer, "returns").Text);
        Assert.Equal("Keep this punctuation: A & B.", Section(integer, "remarks").Text);
        Assert.Contains("A &amp; B", Assert.Single(integer.Documentation.Comments).RawComment);
        Assert.Equal("Docs.Outer<T>", nested.ContainingType);
        Assert.Equal("Docs.Outer<T>.Inner<U>", integer.ContainingType);
        Assert.Equal("class", nested.TypeKind);
        Assert.Equal(new[] { Prefix + "F:Docs.Outer`1.Inner`1.First", Prefix + "F:Docs.Outer`1.Inner`1.Second" }, fields.Select(s => s.Id));
        Assert.All(fields, s => Assert.Equal("Two fields share this declaration.", Section(s, "summary").Text));
        Assert.Equal(Assert.Single(fields[0].Documentation.Comments).Location,
            Assert.Single(fields[1].Documentation.Comments).Location);
        Assert.Equal("property", symbols.Single(s => s.Id == Prefix + "P:Docs.Outer`1.Inner`1.Count").Kind);
        Assert.All(symbols, s => AssertSourceEvidence(fixture, s));
    }

    [Fact]
    public async Task MissingDocumentationAndUnownedCommentsRemainMissingAndExcludedSymbolsStayExcluded()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Ownership.cs", """
            namespace Docs;
            // This ordinary comment is not a documentation declaration.
            public class Missing
            {
                /// <summary>Constructor excluded from this surface.</summary>
                public Missing() { }
                public int Value { get; set; }
                public int Run()
                {
                    // This belongs to a body and must never become Run's description.
                    int Local() => 1;
                    return Local();
                }
                public event System.Action Changed;
                /// <summary>The author says something unrelated to the method name.</summary>
                public int MisleadingName() => 42;
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var run = symbols.Single(s => s.Name == "Run");

        // Assert
        Assert.Equal("missing", run.Documentation.Status);
        Assert.Empty(run.Documentation.Comments);
        Assert.Empty(run.Documentation.Sections);
        Assert.Equal("missing", symbols.Single(s => s.Kind == "type").Documentation.Status);
        Assert.Equal("missing", symbols.Single(s => s.Kind == "property").Documentation.Status);
        Assert.Equal("The author says something unrelated to the method name.",
            Section(symbols.Single(s => s.Name == "MisleadingName"), "summary").Text);
        Assert.DoesNotContain(symbols, s => s.Name is ".ctor" or "get_Value" or "set_Value" or "Local" or "Changed");
        Assert.Contains("constructors", DocumentationCoverage.Current.Exclusions);
        Assert.Contains("ordinary-comment-association", DocumentationCoverage.Current.Exclusions);
    }

    [Fact]
    public async Task PartialDeclarationsAndPartialGenericMethodsKeepAllAuthoredPartsUnderOneIdentity()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Part1.cs", """
            namespace Docs;
            /// <summary>The definition's summary.</summary>
            public partial class Split
            {
                /// <summary>Definition wording.</summary><param name="value">An item.</param>
                partial void Work<T>(T value);
            }
            """), ("Library", "Part2.cs", """
            namespace Docs;
            /// <remarks>The second part's remarks.</remarks>
            public partial class Split
            {
                /// <remarks>Implementation wording.</remarks>
                partial void Work<T>(T value) { }
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var method = symbols.Single(s => s.Id == Prefix + "M:Docs.Split.Work``1(``0)");
        var type = symbols.Single(s => s.Id == Prefix + "T:Docs.Split");

        // Assert
        Assert.Equal(2, method.Documentation.Comments.Length);
        Assert.Equal(2, type.Documentation.Comments.Length);
        Assert.Equal("Definition wording.", Section(method, "summary").Text);
        Assert.Equal("Implementation wording.", Section(method, "remarks").Text);
        Assert.Equal("The second part's remarks.", Section(type, "remarks").Text);
        Assert.Equal(2, type.Locations.Length);
        Assert.All(symbols, s => AssertSourceEvidence(fixture, s));
    }

    [Fact]
    public async Task OverrideInterfaceAndExplicitCrefInheritanceKeepOriginalAuthorAndMapRenamedParametersByOrdinal()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Inheritance.cs", """
            namespace Docs;
            public interface IWorker
            {
                /// <summary>Interface wording.</summary><param name="input">Interface item.</param>
                /// <returns>Interface result.</returns><remarks>Author example one.</remarks>
                int Run(int input);
                /// <summary>Interface count.</summary>
                int Count { get; }
            }
            public class Worker : IWorker
            {
                /// <inheritdoc/>
                public virtual int Run(int renamed) => renamed;
                /// <inheritdoc/>
                public virtual int Count => 1;
            }
            public class Derived : Worker
            {
                /// <inheritdoc/><remarks>The derived author adds a restriction.</remarks>
                public override int Run(int changedAgain) => changedAgain;
                /// <inheritdoc/>
                public override int Count => 2;
                /// <inheritdoc cref="IWorker.Run(int)"/>
                public int Copy(int another) => another;
            }
            public class Explicit : IWorker
            {
                /// <inheritdoc/>
                int IWorker.Run(int explicitName) => explicitName;
                /// <inheritdoc/>
                int IWorker.Count => 3;
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var derived = symbols.Single(s => s.Id == Prefix + "M:Docs.Derived.Run(System.Int32)");
        var copy = symbols.Single(s => s.Name == "Copy");
        var explicitMethod = symbols.Single(s => s.Id == Prefix + "M:Docs.Explicit.Docs#IWorker#Run(System.Int32)");

        // Assert
        Assert.Equal("present", derived.Documentation.Status);
        Assert.Equal("Interface wording.", Section(derived, "summary").Text);
        Assert.Equal("The derived author adds a restriction.", Section(derived, "remarks").Text);
        Assert.Equal("input", Section(derived, "param").Name);
        Assert.Equal("changedAgain", Section(derived, "param").AppliesToName);
        Assert.Equal(Prefix + "M:Docs.IWorker.Run(System.Int32)", Section(derived, "summary").Evidence.SourceSymbolId);
        Assert.Equal(new[] { derived.Id, Prefix + "M:Docs.Worker.Run(System.Int32)" }, Section(derived, "summary").Evidence.InheritedVia);
        Assert.Equal("developer-xml", Section(copy, "summary").Evidence.Origin);
        Assert.Equal("another", Section(copy, "param").AppliesToName);
        Assert.Equal("explicitName", Section(explicitMethod, "param").AppliesToName);
        Assert.Equal("Interface count.", Section(symbols.Single(s => s.Id == Prefix + "P:Docs.Derived.Count"), "summary").Text);
        Assert.All(new[] { derived, copy, explicitMethod }, s => Assert.All(s.Documentation.Inheritance, i => Assert.Equal("resolved", i.Status)));
    }

    [Fact]
    public async Task InheritanceRequiresAnAuthoredRequestAndAmbiguousInterfacesAreNeverChosenByName()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Ambiguous.cs", """
            namespace Docs;
            public interface ILeft { /// <summary>Left wording.</summary>
                int Run(); }
            public interface IRight { /// <summary>Right wording.</summary>
                int Run(); }
            public class Ambiguous : ILeft, IRight
            {
                /// <inheritdoc/>
                public int Run() => 1;
            }
            public class Missing : ILeft
            {
                public int Run() => 2;
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var ambiguous = symbols.Single(s => s.Id == Prefix + "M:Docs.Ambiguous.Run");
        var missing = symbols.Single(s => s.Id == Prefix + "M:Docs.Missing.Run");

        // Assert
        Assert.Equal("unresolved", ambiguous.Documentation.Status);
        Assert.Equal("ambiguous_implicit_target", Assert.Single(ambiguous.Documentation.Inheritance).Status);
        Assert.Empty(ambiguous.Documentation.Sections);
        Assert.Equal("missing", missing.Documentation.Status);
        Assert.Empty(missing.Documentation.Inheritance);
    }

    [Fact]
    public async Task CyclesMalformedXmlUnresolvedCrefIncludesAndUnsupportedPathsStayExplicitAndRetainRawSource()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Failures.cs", """
            namespace Docs;
            public class Failures
            {
                /// <inheritdoc cref="B"/>
                public void A() { }
                /// <inheritdoc cref="A"/>
                public void B() { }
                /// <inheritdoc cref="NoSuchSymbol"/>
                public void Unresolved() { }
                /// <summary>Unclosed developer text.
                public void Malformed() { }
                /// <summary>Ordinary author text.</summary>
                public void Source() { }
                /// <inheritdoc cref="Source" path="/summary"/>
                public void Path() { }
                /// <summary>Prefix <inheritdoc cref="Source"/></summary>
                public void Nested() { }
                /// <include file="not-read.xml" path="/doc"/>
                public void Include() { }
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var a = symbols.Single(s => s.Name == "A");
        var malformed = symbols.Single(s => s.Name == "Malformed");

        // Assert
        Assert.Equal("unresolved", a.Documentation.Status);
        Assert.Contains(a.Documentation.Issues, i => i.Code == "inheritdoc_cycle"
            && i.ResolutionPath.First() == a.Id && i.ResolutionPath.Last() == a.Id);
        Assert.Equal("unresolved_cref", Assert.Single(symbols.Single(s => s.Name == "Unresolved").Documentation.Inheritance).Status);
        Assert.Equal("malformed", malformed.Documentation.Status);
        Assert.Contains("Unclosed developer text.", Assert.Single(malformed.Documentation.Comments).RawComment);
        Assert.Empty(malformed.Documentation.Sections);
        Assert.Equal("unsupported_path", Assert.Single(symbols.Single(s => s.Name == "Path").Documentation.Inheritance).Status);
        Assert.Equal("unsupported_nested", Assert.Single(symbols.Single(s => s.Name == "Nested").Documentation.Inheritance).Status);
        Assert.Contains(symbols.Single(s => s.Name == "Include").Documentation.Issues, i => i.Code == "include_not_expanded");
        Assert.Empty(symbols.Single(s => s.Name == "Include").Documentation.Sections);
    }

    [Fact]
    public async Task CrossProjectInheritanceAndLinkedDeclarationsKeepProjectIdentity()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Contract.cs", """
            namespace Docs;
            public interface IWorker
            {
                /// <summary>Library's exact wording.</summary><param name="input">Library input.</param>
                int Run(int input);
            }
            """), ("Client", "Worker.cs", """
            namespace Docs;
            public class Worker : IWorker
            {
                /// <inheritdoc cref="IWorker.Run(int)"/>
                public int Run(int renamed) => renamed;
            }
            """));
        fixture.AddProjectReference("Client", "Library");
        fixture.AddLinkedDocumentToBoth("Shared.cs", """
            namespace Shared;
            /// <summary>Linked declaration wording.</summary>
            public class Linked { }
            """);

        // Act
        var symbols = await fixture.ExtractAsync();
        var worker = symbols.Single(s => s.Id == "Client/Client.csproj::M:Docs.Worker.Run(System.Int32)");
        var linked = symbols.Where(s => s.Name == "Linked").ToArray();

        // Assert
        Assert.Equal("Library's exact wording.", Section(worker, "summary").Text);
        Assert.Equal(Prefix + "M:Docs.IWorker.Run(System.Int32)", Section(worker, "summary").Evidence.SourceSymbolId);
        Assert.Equal(2, linked.Length);
        Assert.Equal(new[] { "Client/Client.csproj::T:Shared.Linked", Prefix + "T:Shared.Linked" }, linked.Select(s => s.Id));
        Assert.Single(linked.Select(s => s.Location.File).Distinct());
        Assert.All(linked, s => Assert.Equal(s.Id, Section(s, "summary").Evidence.SourceSymbolId));
    }

    [Fact]
    public async Task DocumentationOnlyEditAndInheritedSourceEditChangeCurrentExtractionWithoutChangingStableIds()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Refresh.cs", """
            namespace Docs;
            public class Base
            {
                /// <summary>Original author wording.</summary>
                public virtual void Run() { }
            }
            public class Derived : Base
            {
                /// <inheritdoc/>
                public override void Run() { }
            }
            """));
        var before = await fixture.ExtractAsync();
        var document = fixture.Solution.Projects.Single().Documents.Single();
        var source = await document.GetTextAsync();

        // Act
        fixture.Solution = fixture.Solution.WithDocumentText(document.Id,
            SourceText.From(source.ToString().Replace("Original author wording.", "Revised author wording.", StringComparison.Ordinal)));
        var after = await fixture.ExtractAsync();

        // Assert
        Assert.Equal(before.Select(s => s.Id), after.Select(s => s.Id));
        Assert.Equal("Original author wording.", Section(before.Single(s => s.Id == Prefix + "M:Docs.Derived.Run"), "summary").Text);
        Assert.Equal("Revised author wording.", Section(after.Single(s => s.Id == Prefix + "M:Docs.Derived.Run"), "summary").Text);
        Assert.Contains("Revised author wording.", Assert.Single(after.Single(s => s.Id == Prefix + "M:Docs.Base.Run").Documentation.Comments).RawComment);
    }

    [Fact]
    public async Task GenericCrefInheritanceMapsTypeParametersAndPreservesRepeatedExamplesAndBlockCommentSource()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Generic.cs", """
            namespace Docs;
            public class Generic
            {
                /** <summary>Original block documentation.</summary>
                 * <typeparam name="T">Original type parameter.</typeparam>
                 * <param name="input">Original input parameter.</param>
                 * <example>First authored example.</example><example>Second authored example.</example>
                 */
                public T Echo<T>(T input) => input;
                /// <inheritdoc cref="Echo{R}(R)"/>
                public R Copy<R>(R renamed) => renamed;
                /// <inheritdoc cref="System.String"/>
                public void External() { }
            }
            """));

        // Act
        var symbols = await fixture.ExtractAsync();
        var copy = symbols.Single(s => s.Name == "Copy");
        var original = symbols.Single(s => s.Name == "Echo");

        // Assert
        Assert.Equal("Original block documentation.", Section(copy, "summary").Text);
        Assert.Equal("T", Section(copy, "typeparam").Name);
        Assert.Equal("R", Section(copy, "typeparam").AppliesToName);
        Assert.Equal("renamed", Section(copy, "param").AppliesToName);
        Assert.Equal(new[] { "First authored example.", "Second authored example." },
            copy.Documentation.Sections.Where(s => s.Tag == "example").Select(s => s.Text));
        Assert.StartsWith("/**", Assert.Single(original.Documentation.Comments).RawComment.TrimStart());
        Assert.Equal("metadata_target_unsupported", Assert.Single(symbols.Single(s => s.Name == "External").Documentation.Inheritance).Status);
    }

    [Fact]
    public async Task DeepDocumentationGraphsStopAtAnExplicitBoundary()
    {
        // Arrange
        var declarations = Enumerable.Range(0, 66).Select(i => i == 65
            ? "/// <summary>Terminal author wording.</summary>\npublic void M65() { }"
            : $"/// <inheritdoc cref=\"M{i + 1}\"/>\npublic void M{i}() {{ }}");
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Deep.cs",
            "namespace Docs; public class Deep {\n" + string.Join("\n", declarations) + "\n}"));

        // Act
        var symbols = await fixture.ExtractAsync();

        // Assert
        Assert.Contains(symbols.Single(s => s.Name == "M0").Documentation.Issues, i => i.Code == "inheritdoc_depth_limit");
        Assert.Equal("Terminal author wording.", Section(symbols.Single(s => s.Name == "M65"), "summary").Text);
    }

    [Fact]
    public async Task KindAndDeclaringTypeFiltersPageOneSnapshotAndRejectReplacedGenerations()
    {
        // Arrange
        using var fixture = await DocumentationFixture.CreateAsync(("Library", "Paging.cs", """
            namespace Docs;
            public class First { public int Run(int x) => x; public string Run(string x) => x; }
            public class Second { public int Run { get; set; } }
            """));
        var symbols = await fixture.ExtractAsync();
        var request = new QueryRequest(Type: "Docs.First", Limit: 1);

        // Act
        var first = DocumentationQueries.FindSymbols(symbols, "Run", "all", request, "generation");
        var second = DocumentationQueries.FindSymbols(symbols, "Run", "all",
            request with { Offset = 1, Snapshot = "generation" }, "generation");
        var wrong = Record.Exception(() => DocumentationQueries.FindSymbols(symbols, "Run", "all",
            request with { Offset = 1, Snapshot = "old-generation" }, "generation"));
        var properties = DocumentationQueries.FindSymbols(symbols, "Run", "property", new(), "generation");

        // Assert
        Assert.Equal(2, first.Pagination.Total);
        Assert.Equal(2, first.Items.Concat(second.Items).Select(s => s.Id).Distinct().Count());
        Assert.True(first.Pagination.HasMore);
        Assert.False(second.Pagination.HasMore);
        Assert.Equal("Docs.Second", Assert.Single(properties.Items).ContainingType);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(wrong).Code);
    }

    private static DocumentationSection Section(DocumentedSymbol symbol, string tag) =>
        Assert.Single(symbol.Documentation.Sections, s => s.Tag == tag);

    private static void AssertSourceEvidence(DocumentationFixture fixture, DocumentedSymbol symbol)
    {
        foreach (var comment in symbol.Documentation.Comments)
        {
            var document = fixture.Solution.Projects.SelectMany(p => p.Documents)
                .First(d => Path.GetRelativePath(Path.GetDirectoryName(fixture.SolutionPath)!, d.FilePath!).Replace('\\', '/') == comment.Location.Start.File);
            var source = document.GetTextAsync().GetAwaiter().GetResult();
            var line = source.Lines[comment.Location.Start.Line - 1].ToString();
            Assert.Contains("///", line);
            Assert.True(comment.Location.Start.Column > 0);
            Assert.True(comment.Location.End.Line >= comment.Location.Start.Line);
            Assert.Contains(symbol.Id, comment.Id);
        }
        foreach (var section in symbol.Documentation.Sections.Where(s => s.Evidence.InheritedVia.Length == 0))
            Assert.Contains(symbol.Documentation.Comments, c => c.Id == section.Evidence.CommentId);
    }
}

internal sealed class DocumentationFixture : IDisposable
{
    private readonly AdhocWorkspace workspace = new();
    private readonly Dictionary<string, ProjectId> projects = new(StringComparer.Ordinal);
    public string SolutionPath { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-docs-virtual", "Docs.slnx");
    public Solution Solution { get; set; }

    private DocumentationFixture() => Solution = workspace.CurrentSolution;

    public static async Task<DocumentationFixture> CreateAsync(params (string Project, string File, string Source)[] sources)
    {
        var fixture = new DocumentationFixture();
        foreach (var name in sources.Select(s => s.Project).Distinct())
        {
            var id = ProjectId.CreateNewId();
            fixture.projects.Add(name, id);
            fixture.Solution = fixture.Solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name,
                LanguageNames.CSharp, filePath: Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, name, name + ".csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        }
        foreach (var (project, file, source) in sources)
            fixture.Solution = fixture.Solution.AddDocument(DocumentId.CreateNewId(fixture.projects[project]), file,
                SourceText.From(source), filePath: Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, project, file));
        // Some tests add project references after construction; extraction validates the complete current solution.
        await Task.CompletedTask;
        return fixture;
    }

    public void AddProjectReference(string from, string to) =>
        Solution = Solution.AddProjectReference(projects[from], new ProjectReference(projects[to]));

    public void AddLinkedDocumentToBoth(string file, string source)
    {
        foreach (var project in projects.Values)
            Solution = Solution.AddDocument(DocumentId.CreateNewId(project), file, SourceText.From(source),
                filePath: Path.Combine(Path.GetDirectoryName(SolutionPath)!, file));
    }

    public async Task<DocumentedSymbol[]> ExtractAsync()
    {
        foreach (var project in Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        }
        return await DocumentationCatalog.CreateAsync(Solution, SolutionPath);
    }

    public void Dispose() => workspace.Dispose();
}
