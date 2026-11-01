using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>The <c>find_references</c> tool over the sample solution. Contract: docs/QUERY-CONTRACT.md.</summary>
// Task #05: MCP tool find_references
public sealed class McpFindReferencesTests(McpFindReferencesFixture fixture) : IClassFixture<McpFindReferencesFixture>
{
    private const string Library = "Library/Library.csproj";
    private const string Caller = "Caller/Caller.csproj";
    private const string Calculate = Library + "::M:Sample.Calculator.Calculate(System.Int32)";
    private const string LocalCaller = Library + "::M:Sample.Calculator.LocalCaller";
    private const string InterfaceArea = Library + "::M:Sample.IShape.Area";
    private const string SquareArea = Library + "::M:Sample.Square.Area";
    private const string UnsupportedKind = "References support ordinary and explicit-interface source methods only.";
    private const string NotFound = "Symbol id was not found.";
    private const string Replaced = "The index was replaced. Start a new query at offset zero.";
    private const string OffsetMessage = "offset must be a nonnegative integer.";
    private const string LimitMessage = "limit must be an integer from 1 through 1000.";
    private const string KindsEmpty = "kinds must not be empty.";
    private const string KindUnknown = "Unknown reference kind.";
    private const string SnapshotFailure = "Cannot read the snapshot. Check its path and permissions, or rebuild it.";
    private const string DatabaseFailure = "Cannot read the index database. Check its path and permissions, or rebuild it.";
    private const string UnpairedHigh = "\"\\ud800\"";

    private string Solution => fixture.Solution;
    private string CalculatorFile => Path.Combine(fixture.Root, "Library", "Calculator.cs");

    [Fact]
    public async Task AMethodCalledInTwoFilesReturnsItsInvocationRowsInOrderWithPositionsOnTheName()
    {
        // Arrange
        var lines = new[] { Query(1, Calculate, kinds: ["invocation"]) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        AssertShape(found, success: true);
        Assert.Equal("find_references", found["command"]!.GetValue<string>());
        Assert.Equal(new[] { "references" }, found["result"]!.AsObject().Select(property => property.Key));
        var rows = Rows(responses[0]);
        Assert.Equal(new[] { "Caller/Consumer.cs:7:47", "Library/Calculator.cs:7:40" }, rows.Select(Place));
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "targetId", "kind", "project", "path", "line", "column" }, row.AsObject().Select(property => property.Key));
            Assert.Equal(Calculate, row["targetId"]!.GetValue<string>());
            Assert.Equal("invocation", row["kind"]!.GetValue<string>());
            var source = File.ReadAllLines(Path.Combine(fixture.Root, row["path"]!.GetValue<string>()))[row["line"]!.GetValue<int>() - 1];
            Assert.StartsWith("Calculate(", source[(row["column"]!.GetValue<int>() - 1)..], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(Calculate, null, null)]
    [InlineData(Calculate, Library, null)]
    [InlineData(Calculate, Caller, null)]
    [InlineData(Calculate, null, 1)]
    [InlineData(InterfaceArea, null, null)]
    [InlineData(SquareArea, null, null)]
    [InlineData(LocalCaller, null, null)]
    [InlineData(Calculate, "missing.csproj", null)]
    public async Task RowsMatchTheCli(string id, string? project, int? limit)
    {
        // Arrange
        var cli = await RunCli(CliArguments(id, project, limit));
        Assert.Equal(0, cli.Exit);
        var expected = cli.Json["references"]!.AsArray().Select(row => new JsonObject
        {
            ["targetId"] = row!["definitionId"]!.DeepClone(),
            ["kind"] = row["kind"]!.DeepClone(),
            ["project"] = row["project"]!.DeepClone(),
            ["path"] = row["location"]!["file"]!.DeepClone(),
            ["line"] = row["location"]!["line"]!.DeepClone(),
            ["column"] = row["location"]!["column"]!.DeepClone()
        }).ToArray<JsonNode?>();
        var lines = new[] { Query(1, id, project: project, limit: limit) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.True(JsonNode.DeepEquals(new JsonArray(expected), found["result"]!["references"]));
        Assert.True(JsonNode.DeepEquals(cli.Json["pagination"], found["pagination"]));
        Assert.Equal(cli.Json["warningCount"]!.GetValue<int>(), found["warningCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARowNamesTheDefinitionItsUseResolvedTo()
    {
        // Arrange
        var lines = new[] { Query(1, InterfaceArea) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var rows = Rows(responses[0]);
        Assert.Equal(new[] { SquareArea, InterfaceArea }, rows.Select(row => row["targetId"]!.GetValue<string>()));
    }

    // Task #05: technical review attempt 1. An implicit use writes no name, so the source text at the row pins its
    // position. One Dispose has the Dispose rows of every type. Rows sort by position.
    [Theory]
    [InlineData("M:Sample.Numbers.GetEnumerator", "foreach (var plain", "foreach (var chained")]
    [InlineData("M:Sample.Numbers.MoveNext", "foreach (var plain", "foreach (var chained")]
    [InlineData("M:Sample.Later.GetAwaiter", "await new Later()")]
    [InlineData("M:Sample.Pair.Deconstruct(System.Int32@,System.Int32@)", "var (first", "(left, right) =", "var (head", "var (each")]
    [InlineData("M:Sample.Bag.Add(System.Int32)", "5, 6 }", "6 }")]
    [InlineData("M:Sample.Resource.Dispose", "using (var block", "using var scoped", "var held", "using (held)", "var plain", "Resource typed", "var other", "var task", "object converted", "var derived", "var counted")]
    [InlineData("M:Sample.Ticker.Dispose", "using (var block", "using var scoped", "var held", "using (held)", "var plain", "Resource typed", "var other", "var task", "object converted", "var derived", "var counted")]
    [InlineData("M:Sample.Loose.Dispose", "var loose")]
    [InlineData("M:Sample.Bag.GetEnumerator", "foreach (var (each", "foreach (var viaArray")]
    [InlineData("M:Sample.Walker.MoveNext", "foreach (var (each", "foreach (var viaArray", "foreach (var viaList")]
    [InlineData("M:Sample.Outer.Deconstruct(System.Int32@,Sample.Pair@)", "var (head")]
    [InlineData("M:Sample.Collected.Add(System.Int32)", "7 }")]
    [InlineData("M:Sample.Both.Dispose", "using (var block", "using var scoped", "var held", "using (held)", "var plain", "Resource typed", "var other", "var task", "object converted", "var derived", "var counted")]
    [InlineData("M:Sample.AsyncResource.DisposeAsync")]
    [InlineData("M:Sample.AsyncNumbers.GetAsyncEnumerator(System.Threading.CancellationToken)")]
    [InlineData("M:Sample.Words.Select(System.Func{System.Int32,System.Int32})")]
    [InlineData("M:Sample.OpenDispose.Dispose", "var open")]
    [InlineData("M:Sample.InternalTypeDispose.Dispose", "var internalType")]
    [InlineData("M:Sample.DisposeRule.Nested.Dispose", "var nested")]
    [InlineData("M:Sample.StaticDispose.Dispose", "var shared")]
    [InlineData("M:Sample.GenericDispose.Dispose``1", "var generic")]
    [InlineData("M:Sample.PrivateDispose.Dispose")]
    [InlineData("M:Sample.ProtectedDispose.Dispose")]
    [InlineData("M:Sample.InternalDispose.Dispose")]
    [InlineData("M:Sample.ProtectedInternalDispose.Dispose")]
    [InlineData("M:Sample.PrivateProtectedDispose.Dispose")]
    public async Task AnImplicitUseIsAReferenceAtTheSyntaxThatCausesIt(string method, params string[] expected)
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::" + method) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var rows = Rows(responses[0]);
        Assert.Equal(expected.Length, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal("reference", rows[index]["kind"]!.GetValue<string>());
            Assert.StartsWith(expected[index], TextAt(rows[index]), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnImplicitDisposeNamesTheDisposeOfItsInitializersTypeOrTheQueriedMethodWhenThatIsNotInSource()
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::M:Sample.Resource.Dispose") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var targets = Rows(responses[0]).ToDictionary(TextAt, row => row["targetId"]!.GetValue<string>());
        Assert.Equal(Library + "::M:Sample.OtherResource.Dispose", targets.Single(row => row.Key.StartsWith("var other", StringComparison.Ordinal)).Value);
        Assert.Equal(Library + "::M:Sample.Resource.Dispose", targets.Single(row => row.Key.StartsWith("var task", StringComparison.Ordinal)).Value);
        Assert.Equal(Library + "::M:Sample.Resource.Dispose", targets.Single(row => row.Key.StartsWith("var plain", StringComparison.Ordinal)).Value);
        Assert.Equal(Library + "::M:Sample.Resource.Dispose", targets.Single(row => row.Key.StartsWith("object converted", StringComparison.Ordinal)).Value);
        Assert.Equal(Library + "::M:Sample.Parent.Dispose", targets.Single(row => row.Key.StartsWith("var derived", StringComparison.Ordinal)).Value);
    }

    // Task #05: technical review attempt 2. The foreach rows resolve to IEnumerator.MoveNext and the calls to ToString
    // outside the source, so no stored definition exists for them and the row falls back to the queried id.
    [Theory]
    [InlineData("M:Sample.Walker.MoveNext", "foreach (var viaArray")]
    [InlineData("M:Sample.Square.ToString", "ToString() ?? \"\";", "ToString();")]
    public async Task AUseOfARelatedDefinitionOutsideTheSourceNamesTheQueriedId(string method, params string[] expected)
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::" + method) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var rows = Rows(responses[0]);
        Assert.All(rows, row => Assert.Equal(Library + "::" + method, row["targetId"]!.GetValue<string>()));
        Assert.All(expected, text => Assert.Contains(rows, row => TextAt(row).StartsWith(text, StringComparison.Ordinal)));
    }

    // Task #05: technical review attempt 2. NoUsing.cs has the same local as OnlyAwaitUsing.cs, a using directive and
    // the name Dispose, so nothing but the missing using statement keeps it out of these rows.
    [Fact]
    public async Task AFileWithNoUsingStatementOrDeclarationHasNoDisposeRows()
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::M:Sample.Resource.Dispose") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var rows = Rows(responses[0]);
        Assert.Equal(new[] { "Library/ImplicitUses.cs", "Library/OnlyAwaitUsing.cs" }, rows.Select(row => row["path"]!.GetValue<string>()).Distinct());
        Assert.Equal("var counted = new Resource();", TextAt(rows.Single(row => row["path"]!.GetValue<string>() == "Library/OnlyAwaitUsing.cs")));
    }

    // Task #05: QA attempt 1. Both Dispose methods implement IDisposable.Dispose, so they carry the rows of every type.
    // A type parameter constrained to the interface or to Parent would show up here, and DisposeRule.cs has none.
    [Fact]
    public async Task ATypeParameterHasNoDisposeRowWhateverItsConstraint()
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::M:Sample.Resource.Dispose"), Query(2, Library + "::M:Sample.Parent.Dispose") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.DoesNotContain(Rows(responses[0]).Concat(Rows(responses[1])), row => row["path"]!.GetValue<string>() == "Library/DisposeRule.cs");
    }

    [Fact]
    public async Task AColumnCountsUtf16CodeUnitsFromTheStartOfTheLine()
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::M:Sample.Columns.Target") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var rows = Rows(responses[0]);
        Assert.Equal(new[] { "Library/Columns.cs:7:2", "Library/Columns.cs:8:9", "Library/Columns.cs:9:10" }, rows.Select(Place));
        Assert.All(rows, row => Assert.StartsWith("Target()", TextAt(row), StringComparison.Ordinal));
    }

    // Task #05: technical review attempt 1. These rows pin what the index stores, not what a kind should mean:
    // task 18 changes the classifier, and this test with it.
    [Theory]
    [InlineData("M:Sample.Chain.Zero", "Wrap(Zero).Run();", "invocation", "Wrap(Zero);", "reference", "Wrap(Zero)?.Run();", "reference", "Zero();", "invocation")]
    [InlineData("M:Sample.Chain.Target", "Mark(nameof(Target)).Run();", "invocation", "Mark(nameof(Target));", "reference")]
    public async Task ANameInsideACalleeExpressionIsStoredAsAnInvocationEvenWhenNothingCallsIt(string method, params string[] expected)
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::" + method) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var stored = Rows(responses[0]).SelectMany(row => new[] { LineOf(row).Trim(), row["kind"]!.GetValue<string>() });
        Assert.Equal(expected, stored);
    }

    // Task #05: technical review attempt 2. A cref is on the name and follows the rule of every other written name,
    // so the one inside a lambda that lies in a callee expression is stored as an invocation.
    [Fact]
    public async Task ACrefOfADocumentationCommentIsStoredOnTheNameByTheCalleeRule()
    {
        // Arrange
        var lines = new[] { Query(1, Library + "::M:Sample.Chain.Documented") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var stored = Rows(responses[0]).Select(row => $"{row["kind"]!.GetValue<string>()} {TextAt(row)}");
        Assert.Equal(new[] { "reference Documented\"/>.</summary>", "invocation Documented\"/>.</summary>" }, stored);
    }

    [Fact]
    public async Task KindsAndProjectFiltersKeepOnlyMatchingRows()
    {
        // Arrange
        var lines = new[]
        {
            Query(1, Calculate),
            Query(2, Calculate, kinds: ["invocation"]),
            Query(3, Calculate, kinds: ["reference"]),
            Query(4, Calculate, kinds: ["reference", "invocation"]),
            Query(5, Calculate, kinds: ["invocation", "invocation"]),
            Query(6, Calculate, project: Library),
            Query(7, Calculate, project: Caller),
            Query(8, Calculate, kinds: ["invocation"], project: Caller),
            Query(9, Calculate, kinds: ["reference"], project: Library),
            Query(10, Calculate, kinds: ["reference"], limit: 1)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Equal(new[] { "Caller/Consumer.cs:7:47", "Caller/Consumer.cs:9:58", "Library/Calculator.cs:7:40" }, Rows(responses[0]).Select(Place));
        Assert.Equal(new[] { "Caller/Consumer.cs:7:47", "Library/Calculator.cs:7:40" }, Rows(responses[1]).Select(Place));
        var reference = Assert.Single(Rows(responses[2]));
        Assert.Equal("Caller/Consumer.cs:9:58", Place(reference));
        Assert.Equal("reference", reference["kind"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Result(responses[0]), Result(responses[3])));
        Assert.True(JsonNode.DeepEquals(Result(responses[1]), Result(responses[4])));
        Assert.Equal("Library/Calculator.cs:7:40", Place(Assert.Single(Rows(responses[5]))));
        Assert.All(Rows(responses[6]), row => Assert.Equal(Caller, row["project"]!.GetValue<string>()));
        Assert.Equal(2, Rows(responses[6]).Length);
        Assert.Equal("Caller/Consumer.cs:7:47", Place(Assert.Single(Rows(responses[7]))));
        Assert.Empty(Rows(responses[8]));
        Assert.Equal(1, Envelope(responses[9], isError: false)["pagination"]!["total"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("missing.csproj")]
    [InlineData("library/library.csproj")]
    [InlineData("Library\\Library.csproj")]
    [InlineData("' OR 1=1 --")]
    [InlineData("\u00e9\u4e2d")]
    public async Task AnUnknownProjectIsAnEmptyPage(string project)
    {
        // Arrange
        var lines = new[] { Query(1, Calculate, project: project) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.Empty(found["result"]!["references"]!.AsArray());
        Assert.Equal(0, found["pagination"]!["total"]!.GetValue<int>());
        Assert.False(found["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(found["pagination"]!["nextOffset"]);
        Assert.True(found["pagination"]!["continuationSupported"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("Library/Library.csproj::M:Sample.Calculator.Missing")]
    [InlineData("library/library.csproj::m:sample.calculator.calculate(system.int32)")]
    [InlineData(Calculate + " ")]
    [InlineData(" " + Calculate)]
    [InlineData("   ")]
    [InlineData("::")]
    [InlineData("%")]
    [InlineData("*")]
    [InlineData("'; DROP TABLE source_references; --")]
    [InlineData("\"quoted\"")]
    [InlineData("a\\b/c?d#e")]
    [InlineData("\u0000")]
    [InlineData("Library/Library.csproj::M:Sample.Gr\u00f6\u00dfe")]
    [InlineData("\ud83d\ude00")]
    [InlineData("Library/Library.csproj::M:Sample.Square.#ctor")]
    [InlineData("Library/Library.csproj::M:Sample.Square.get_Sides")]
    [InlineData("Library/Library.csproj::M:Sample.Square.op_Addition(Sample.Square,Sample.Square)")]
    [InlineData("Library/Library.csproj::E:Sample.Square.Changed")]
    [InlineData("Library/Library.csproj::M:Sample.Point.Deconstruct(System.Int32@)")]
    public async Task AnUnknownIdReturnsSymbolNotFoundWithNoFreshnessCoverageOrPagination(string id)
    {
        // Arrange
        var lines = new[] { Query(1, id), Query(2, Calculate) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertRefused(responses[0], "symbol_not_found", NotFound);
        Assert.Equal(3, Rows(responses[1]).Length);
    }

    [Fact]
    public async Task AVeryLongIdReturnsSymbolNotFound()
    {
        // Arrange
        var lines = new[] { Query(1, new string('a', 1_000_000)) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertRefused(responses[0], "symbol_not_found", NotFound);
    }

    [Fact]
    public async Task AKnownMethodWithNoUsesIsAnEmptyPageNotSymbolNotFound()
    {
        // Arrange
        var lines = new[] { Query(1, LocalCaller) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.Empty(found["result"]!["references"]!.AsArray());
        Assert.Equal(0, found["pagination"]!["total"]!.GetValue<int>());
        Assert.NotNull(found["freshness"]);
        Assert.NotNull(found["coverage"]);
    }

    [Theory]
    [InlineData("Library/Library.csproj::T:Sample.Calculator")]
    [InlineData("Library/Library.csproj::T:Sample.IShape")]
    [InlineData("Library/Library.csproj::P:Sample.Square.Sides")]
    [InlineData("Library/Library.csproj::F:Sample.Square.Corners")]
    public async Task ANonMethodIdReturnsUnsupportedSymbolKind(string id)
    {
        // Arrange
        var lines = new[] { Query(1, id), Query(2, Calculate) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertRefused(responses[0], "unsupported_symbol_kind", UnsupportedKind);
        Assert.Equal(3, Rows(responses[1]).Length);
    }

    [Fact]
    public async Task PagesOnOneServerContinueWithTheReturnedSnapshotIdAndJoinToTheWholeList()
    {
        // Arrange
        var generation = await Generation();
        var lines = new[]
        {
            Query(1, Calculate),
            Query(2, Calculate, limit: 1),
            Query(3, Calculate, offset: 1, limit: 1, snapshotId: generation),
            Query(4, Calculate, offset: 2, limit: 1, snapshotId: generation),
            Query(5, Calculate, offset: 3, limit: 1, snapshotId: generation),
            Query(6, Calculate, kinds: ["invocation"], offset: 1, limit: 1, snapshotId: generation),
            Query(7, Calculate, project: Caller, offset: 1, snapshotId: generation)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var whole = Rows(responses[0]);
        Assert.Equal(3, whole.Length);
        for (var page = 0; page < 3; page++)
        {
            var pageEnvelope = Envelope(responses[page + 1], isError: false);
            var pagination = pageEnvelope["pagination"]!;
            Assert.True(JsonNode.DeepEquals(whole[page], pageEnvelope["result"]!["references"]![0]));
            Assert.Equal(page, pagination["offset"]!.GetValue<int>());
            Assert.Equal(1, pagination["returned"]!.GetValue<int>());
            Assert.Equal(3, pagination["total"]!.GetValue<int>());
            Assert.Equal(generation, pagination["snapshotId"]!.GetValue<string>());
            Assert.True(pagination["continuationSupported"]!.GetValue<bool>());
            Assert.Equal(page < 2, pagination["hasMore"]!.GetValue<bool>());
            Assert.Equal(page < 2 ? page + 1 : null, pagination["nextOffset"]?.GetValue<int>());
        }
        var past = Envelope(responses[4], isError: false);
        Assert.Empty(past["result"]!["references"]!.AsArray());
        Assert.Equal(3, past["pagination"]!["total"]!.GetValue<int>());
        Assert.False(past["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(past["pagination"]!["nextOffset"]);
        Assert.Equal("Library/Calculator.cs:7:40", Place(Assert.Single(Rows(responses[5]))));
        Assert.Equal("Caller/Consumer.cs:9:58", Place(Assert.Single(Rows(responses[6]))));
    }

    [Fact]
    public async Task BoundaryLimitsAndOffsetsSucceed()
    {
        // Arrange
        var generation = await Generation();
        var lines = new[]
        {
            Query(1, Calculate, limit: 1),
            Query(2, Calculate, limit: 1000),
            Query(3, Calculate, offset: int.MaxValue, limit: 1000, snapshotId: generation),
            Query(4, Calculate, offset: 0, snapshotId: generation)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Single(Rows(responses[0]));
        Assert.Equal(1000, Envelope(responses[1], isError: false)["pagination"]!["limit"]!.GetValue<int>());
        Assert.Equal(3, Rows(responses[1]).Length);
        var last = Envelope(responses[2], isError: false);
        Assert.Empty(last["result"]!["references"]!.AsArray());
        Assert.Equal(int.MaxValue, last["pagination"]!["offset"]!.GetValue<int>());
        Assert.Equal(3, last["pagination"]!["total"]!.GetValue<int>());
        Assert.False(last["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(last["pagination"]!["nextOffset"]);
        Assert.Equal(3, Rows(responses[3]).Length);
    }

    [Fact]
    public async Task OneServerRefusesAPageOfAReplacedGenerationAndContinuesOnTheNewOne()
    {
        // Arrange
        var previous = await Generation();
        var lines = new[]
        {
            Query(1, Calculate, limit: 1),
            Query(2, Calculate, offset: 1, limit: 1, snapshotId: previous),
            Tool(3, "index", new JsonObject { ["workspace"] = fixture.Root }),
            Query(4, Calculate, offset: 2, limit: 1, snapshotId: previous),
            Query(5, Calculate, snapshotId: previous),
            Query(6, Calculate, limit: 1)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Equal(previous, Envelope(responses[0], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Single(Rows(responses[1]));
        var published = Envelope(responses[2], isError: false)["result"]!["generationId"]!.GetValue<string>();
        Assert.NotEqual(previous, published);
        AssertRefused(responses[3], "snapshot_mismatch", Replaced);
        AssertRefused(responses[4], "snapshot_mismatch", Replaced);
        Assert.Equal(published, Envelope(responses[5], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASnapshotIdThatIsNotExactlyTheGenerationIsRefusedAtAnyOffset()
    {
        // Arrange
        var generation = await Generation();
        var lines = new[]
        {
            Query(1, Calculate, snapshotId: "previous-snapshot"),
            Query(2, Calculate, offset: 1, snapshotId: "previous-snapshot"),
            Query(3, Calculate, snapshotId: generation[1..]),
            Query(4, Calculate, snapshotId: generation + " "),
            Query(5, Calculate, snapshotId: generation + ":ai:0"),
            Query(6, "missing", snapshotId: "previous-snapshot")
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.All(responses, response => AssertRefused(response, "snapshot_mismatch", Replaced));
    }

    [Fact]
    public async Task ASnapshotIdInAnotherCaseIsRefused()
    {
        // Arrange
        var generation = await Generation();
        var lines = new[] { Query(1, Calculate, snapshotId: Swap(generation)) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.All(responses, response => AssertRefused(response, "snapshot_mismatch", Replaced));
    }

    [Fact]
    public async Task AnEditedInputIsRefusedAsStaleWithoutStoppingTheServer()
    {
        // Arrange
        var original = await File.ReadAllBytesAsync(CalculatorFile);
        var lines = new[]
        {
            Query(1, Calculate),
            Query(2, Calculate),
            Query(3, "missing"),
            Query(4, "Library/Library.csproj::T:Sample.Calculator"),
            Request(5, "tools/list")
        };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.AppendAllText(CalculatorFile, Environment.NewLine + "// changed");
            });

            // Assert
            Envelope(responses[0], isError: false);
            foreach (var response in responses[1..4])
            {
                var stale = Envelope(response, isError: true);
                Assert.Equal("find_references", stale["command"]!.GetValue<string>());
                Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
                Assert.Equal("The saved index is stale.", stale["error"]!["message"]!.GetValue<string>());
                Assert.True(stale["freshness"]!["isStale"]!.GetValue<bool>());
                Assert.Contains("Library/Calculator.cs", stale["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
                Assert.NotNull(stale["coverage"]);
                Assert.False(stale.ContainsKey("pagination"));
                Assert.False(stale.ContainsKey("result"));
            }
            Assert.Contains("find_references", ToolNames(responses[4]));
        }
        finally
        {
            await File.WriteAllBytesAsync(CalculatorFile, original);
        }
    }

    [Fact]
    public async Task AReplacedSnapshotIdIsReportedBeforeStaleness()
    {
        // Arrange
        var original = await File.ReadAllBytesAsync(CalculatorFile);
        var lines = new[] { Query(1, Calculate, snapshotId: "previous-snapshot") };
        await File.AppendAllTextAsync(CalculatorFile, Environment.NewLine + "// changed");
        try
        {
            // Act
            var responses = await Talk(Solution, lines);

            // Assert
            AssertRefused(responses[0], "snapshot_mismatch", Replaced);
        }
        finally
        {
            await File.WriteAllBytesAsync(CalculatorFile, original);
        }
    }

    [Fact]
    public async Task AChangedInputThatCannotBeReadIsRefusedAsAnIoFailure()
    {
        // Arrange
        var original = await File.ReadAllBytesAsync(CalculatorFile);
        FileStream? held = null;
        var lines = new[] { Query(1, Calculate), Query(2, Calculate) };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index != 1) return;
                File.AppendAllText(CalculatorFile, "// changed");
                held = new FileStream(CalculatorFile, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var failed = Envelope(responses[1], isError: true);
            Assert.Equal("find_references", failed["command"]!.GetValue<string>());
            Assert.Equal("snapshot_io_failed", failed["error"]!["code"]!.GetValue<string>());
            Assert.Equal("Snapshot inputs could not be verified. Publication was not committed; resolve the input access failure and retry.",
                failed["error"]!["message"]!.GetValue<string>());
            Assert.NotNull(failed["freshness"]);
            Assert.NotNull(failed["coverage"]);
            Assert.False(failed.ContainsKey("pagination"));
        }
        finally
        {
            held?.Dispose();
            await File.WriteAllBytesAsync(CalculatorFile, original);
        }
    }

    // Task #05: technical review attempt 1. The store fails before freshness is known, so both are null. Line 0 holds
    // the file before the server has loaded anything, and line 1 after a query has made the session warm.
    [Theory]
    [InlineData("current.json", 0, SnapshotFailure)]
    [InlineData("current.json", 1, SnapshotFailure)]
    [InlineData("overview.md", 0, SnapshotFailure)]
    [InlineData("overview.md", 1, SnapshotFailure)]
    [InlineData("index.db", 0, DatabaseFailure)]
    [InlineData("index.db", 1, DatabaseFailure)]
    public async Task AStoreFileThatCannotBeReadIsRefusedWithNoFreshnessOrCoverage(string file, int heldFromLine, string message)
    {
        // Arrange
        // SQLite ignores the lock FileShare.None takes on Unix, so only Windows refuses the database this way.
        if (file == "index.db" && !OperatingSystem.IsWindows()) return;
        var generation = (await IndexStore.LoadSnapshotAsync(fixture.IndexPath)).GenerationDirectory!;
        var path = file == "current.json" ? fixture.IndexPath : Path.Combine(generation, file);
        FileStream? held = null;
        var lines = new[] { Query(1, Calculate), Query(2, Calculate) };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == heldFromLine) held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            Assert.All(responses[heldFromLine..], response => AssertRefused(response, "snapshot_io_failed", message));
            Assert.All(responses[..heldFromLine], response => Assert.Equal(3, Rows(response).Length));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task ADamagedOrUnsupportedManifestIsRefusedAndAnUnpublishedSolutionFindsNoSymbol()
    {
        // Arrange
        var pointer = await File.ReadAllBytesAsync(fixture.IndexPath);
        var generation = await Generation();
        var lines = new[]
        {
            Query(1, Calculate),
            Query(2, Calculate),
            Query(3, Calculate),
            Query(4, Calculate),
            Query(5, Calculate, snapshotId: generation),
            Query(6, Calculate)
        };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.WriteAllText(fixture.IndexPath, "{");
                if (index == 2) File.WriteAllText(fixture.IndexPath, """{"manifestVersion":99}""");
                if (index == 3) File.Delete(fixture.IndexPath);
                if (index == 5) File.WriteAllBytes(fixture.IndexPath, pointer);
            });

            // Assert
            Assert.Equal(3, Rows(responses[0]).Length);
            AssertRefused(responses[1], "invalid_index");
            AssertRefused(responses[2], "unsupported_index");
            AssertRefused(responses[3], "symbol_not_found", NotFound);
            AssertRefused(responses[4], "snapshot_mismatch", Replaced);
            Assert.Equal(3, Rows(responses[5]).Length);
        }
        finally
        {
            await File.WriteAllBytesAsync(fixture.IndexPath, pointer);
        }
    }

    [Fact]
    public async Task AnUnavailableSqliteLibraryIsRefused()
    {
        // Arrange
        var lines = new[] { Query(1, Calculate) };
        SqliteAvailability.Probe = () => new SqliteStatus(false, null, false, "Injected failure.");
        try
        {
            // Act
            var responses = await Talk(Solution, lines);

            // Assert
            AssertRefused(responses[0], "sqlite_unavailable");
        }
        finally
        {
            SqliteAvailability.Probe = null;
        }
    }

    [Theory]
    [InlineData("workspace", null, "workspace is required.")]
    [InlineData("workspace", "7", "workspace is required.")]
    [InlineData("workspace", "null", "workspace is required.")]
    [InlineData("workspace", UnpairedHigh, "workspace is required.")]
    [InlineData("workspace", "\"\"", "workspace must not be empty.")]
    [InlineData("workspace", "\"elsewhere\"", "workspace must be the solution directory.")]
    [InlineData("id", null, "id is required.")]
    [InlineData("id", "7", "id is required.")]
    [InlineData("id", "null", "id is required.")]
    [InlineData("id", "[]", "id is required.")]
    [InlineData("id", UnpairedHigh, "id is required.")]
    [InlineData("id", "\"words \\udc00 words\"", "id is required.")]
    [InlineData("id", "\"\"", "id must not be empty.")]
    [InlineData("kinds", "[]", KindsEmpty)]
    [InlineData("kinds", "7", KindsEmpty)]
    [InlineData("kinds", "true", KindsEmpty)]
    [InlineData("kinds", "{}", KindsEmpty)]
    [InlineData("kinds", "\"invocation\"", KindsEmpty)]
    [InlineData("kinds", "\"\"", KindsEmpty)]
    [InlineData("kinds", UnpairedHigh, KindsEmpty)]
    [InlineData("kinds", "[\"call\"]", KindUnknown)]
    [InlineData("kinds", "[\"\"]", KindUnknown)]
    [InlineData("kinds", "[\"Invocation\"]", KindUnknown)]
    [InlineData("kinds", "[\" invocation\"]", KindUnknown)]
    [InlineData("kinds", "[\"template-call\"]", KindUnknown)]
    [InlineData("kinds", "[\"invocation\",\"call\"]", KindUnknown)]
    [InlineData("kinds", "[7]", KindUnknown)]
    [InlineData("kinds", "[null]", KindUnknown)]
    [InlineData("kinds", "[true]", KindUnknown)]
    [InlineData("kinds", "[[\"invocation\"]]", KindUnknown)]
    [InlineData("kinds", "[{}]", KindUnknown)]
    [InlineData("kinds", "[\"invocation\",7]", KindUnknown)]
    [InlineData("kinds", "[" + UnpairedHigh + "]", KindUnknown)]
    [InlineData("project", "\"\"", "project must not be empty.")]
    [InlineData("project", "7", "project must not be empty.")]
    [InlineData("project", UnpairedHigh, "project must not be empty.")]
    [InlineData("snapshotId", "\"\"", "snapshotId must not be empty.")]
    [InlineData("snapshotId", "7", "snapshotId must not be empty.")]
    [InlineData("snapshotId", UnpairedHigh, "snapshotId must not be empty.")]
    [InlineData("offset", "-1", OffsetMessage)]
    [InlineData("offset", "1.5", OffsetMessage)]
    [InlineData("offset", "1.0", OffsetMessage)]
    [InlineData("offset", "1e2", OffsetMessage)]
    [InlineData("offset", "\"1\"", OffsetMessage)]
    [InlineData("offset", "true", OffsetMessage)]
    [InlineData("offset", "[]", OffsetMessage)]
    [InlineData("offset", "2147483648", OffsetMessage)]
    [InlineData("offset", "-2147483649", OffsetMessage)]
    [InlineData("offset", "99999999999999999999", OffsetMessage)]
    [InlineData("offset", UnpairedHigh, OffsetMessage)]
    [InlineData("offset", "1", "offset greater than zero requires snapshotId.")]
    [InlineData("limit", "0", LimitMessage)]
    [InlineData("limit", "-1", LimitMessage)]
    [InlineData("limit", "1001", LimitMessage)]
    [InlineData("limit", "1.5", LimitMessage)]
    [InlineData("limit", "1.0", LimitMessage)]
    [InlineData("limit", "1e2", LimitMessage)]
    [InlineData("limit", "\"10\"", LimitMessage)]
    [InlineData("limit", "true", LimitMessage)]
    [InlineData("limit", "{}", LimitMessage)]
    [InlineData("limit", "2147483648", LimitMessage)]
    [InlineData("limit", UnpairedHigh, LimitMessage)]
    public async Task EachInvalidArgumentReturnsItsDocumentedMessage(string field, string? value, string message)
    {
        // Arrange
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["id"] = Calculate };
        arguments.Remove(field);
        var line = value is null ? Tool(1, "find_references", arguments) : RawTool(1, "find_references", arguments, field, value);
        var lines = new[] { line, Request(2, "tools/list") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertRefused(responses[0], "invalid_arguments", message);
        Assert.Contains("find_references", ToolNames(responses[1]));
    }

    [Fact]
    public async Task AnOffsetWithANullSnapshotIdIsRefused()
    {
        // Arrange
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["id"] = Calculate, ["offset"] = 1 };
        var lines = new[] { RawTool(1, "find_references", arguments, "snapshotId", "null") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertRefused(responses[0], "invalid_arguments", "offset greater than zero requires snapshotId.");
    }

    // Many JSON clients send null for an unset optional field, so it means the same as omitting it.
    [Theory]
    [InlineData("kinds")]
    [InlineData("project")]
    [InlineData("snapshotId")]
    [InlineData("offset")]
    [InlineData("limit")]
    public async Task NullForAnOptionalInputReturnsTheSameRowsAsOmittingIt(string field)
    {
        // Arrange
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["id"] = Calculate };
        var lines = new[] { Query(1, Calculate), RawTool(2, "find_references", arguments, field, "null") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var omitted = Envelope(responses[0], isError: false);
        var sentNull = Envelope(responses[1], isError: false);
        Assert.NotEmpty(omitted["result"]!["references"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(omitted["result"], sentNull["result"]));
        Assert.True(JsonNode.DeepEquals(omitted["pagination"], sentNull["pagination"]));
    }

    [Fact]
    public async Task OtherArgumentsAreIgnored()
    {
        // Arrange
        var arguments = new JsonObject
        {
            ["workspace"] = fixture.Root, ["id"] = Calculate, ["kind"] = "not-a-kind", ["name"] = 7, ["type"] = "Sample.Calculator"
        };
        var lines = new[] { Tool(1, "find_references", arguments) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Equal(3, Rows(responses[0]).Length);
    }

    [Fact]
    public async Task ToolsListDescribesTheTool()
    {
        // Arrange
        var lines = new[] { Request(1, "tools/list") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Equal(new[] { "find_symbol", "search_symbols", "find_references", "index_status", "coverage", "index" }, ToolNames(responses[0]));
        var tool = responses[0]["result"]!["tools"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "find_references")!;
        var schema = tool["inputSchema"]!;
        Assert.Equal(new[] { "workspace", "id" }, schema["required"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(new[] { "workspace", "id", "kinds", "project", "offset", "limit", "snapshotId" },
            schema["properties"]!.AsObject().Select(property => property.Key));
        Assert.Equal("array", schema["properties"]!["kinds"]!["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["kinds"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", schema["properties"]!["limit"]!["type"]!.GetValue<string>());
        Assert.False(schema["properties"]!["id"]!.AsObject().ContainsKey("items"));
    }

    [Fact]
    public async Task ASecondQueryIsServedWarmWithoutHashingUnchangedInputsAgain()
    {
        // Arrange
        FileStream? held = null;
        SnapshotInputs.ResetFullFreshnessChecks(Solution);
        var lines = new[] { Query(1, Calculate), Query(2, Calculate, limit: 1) };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                // A hash of this file would now fail, so a successful answer proves it was not read.
                if (index == 1) held = new FileStream(CalculatorFile, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            Envelope(responses[0], isError: false);
            Envelope(responses[1], isError: false);
            Assert.Equal(1, SnapshotInputs.FullFreshnessChecks(Solution));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task QueriesWriteNothing()
    {
        // Arrange
        var store = Path.Combine(fixture.Root, ".codeatlas");
        var before = Contents(store);
        var lines = new[] { Query(1, Calculate), Query(2, "missing"), Query(3, Calculate, kinds: ["invocation"]) };

        // Act
        await Talk(Solution, lines);

        // Assert
        Assert.Equal(before, Contents(store));
    }

    [Fact]
    public async Task TheCoverageOfASuccessfulQueryKeepsItsLimitations()
    {
        // Arrange
        var lines = new[] { Query(1, Calculate) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.Contains("multi-targeting-not-fully-covered", found["coverage"]!["limitations"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(found["coverage"]!["runtimeCallGraph"]!.GetValue<bool>());
    }

    private string Query(int id, string symbol, string[]? kinds = null, string? project = null,
        int? offset = null, int? limit = null, string? snapshotId = null)
    {
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["id"] = symbol };
        if (kinds is not null) arguments["kinds"] = new JsonArray(kinds.Select(kind => (JsonNode?)kind).ToArray());
        if (project is not null) arguments["project"] = project;
        if (offset is not null) arguments["offset"] = offset;
        if (limit is not null) arguments["limit"] = limit;
        if (snapshotId is not null) arguments["snapshotId"] = snapshotId;
        return Tool(id, "find_references", arguments);
    }

    private string[] CliArguments(string id, string? project, int? limit)
    {
        var arguments = new List<string> { "find-references", "--index", fixture.IndexPath, "--symbol", id };
        if (project is not null) arguments.AddRange(["--project", project]);
        if (limit is not null) arguments.AddRange(["--limit", limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return [.. arguments];
    }

    private async Task<string> Generation() => (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).Index.IndexId;

    private static string Swap(string generation) =>
        new(generation.Select(character => char.IsLower(character) ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character)).ToArray());

    private static JsonNode Result(JsonObject response) => Envelope(response, isError: false)["result"]!;

    private static JsonNode[] Rows(JsonObject response) => Result(response)["references"]!.AsArray().Select(row => row!).ToArray();

    private static string Place(JsonNode row) =>
        $"{row["path"]!.GetValue<string>()}:{row["line"]!.GetValue<int>()}:{row["column"]!.GetValue<int>()}";

    private string LineOf(JsonNode row) =>
        File.ReadAllLines(Path.Combine(fixture.Root, row["path"]!.GetValue<string>()))[row["line"]!.GetValue<int>() - 1];

    private string TextAt(JsonNode row) => LineOf(row)[(row["column"]!.GetValue<int>() - 1)..];

    private static string[] ToolNames(JsonObject response) =>
        response["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

    private static string[] Contents(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => file + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

    private static void AssertRefused(JsonObject response, string code, string? message = null)
    {
        var refused = Envelope(response, isError: true);
        Assert.Equal("find_references", refused["command"]!.GetValue<string>());
        Assert.Equal(code, refused["error"]!["code"]!.GetValue<string>());
        if (message is not null) Assert.Equal(message, refused["error"]!["message"]!.GetValue<string>());
        Assert.Null(refused["freshness"]);
        Assert.Null(refused["coverage"]);
        Assert.False(refused.ContainsKey("pagination"));
        Assert.False(refused.ContainsKey("result"));
    }
}

public sealed class McpFindReferencesNoIndexTests
{
    [Fact]
    public async Task WithNoPublishedIndexTheIdIsNotFoundUnlessASnapshotIdWasSupplied()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("codeatlas-mcp-references-").FullName;
        var solution = Path.Combine(root, "Sample.slnx");
        File.Copy(Path.Combine(Repository(), "tests", "Fixtures", "Sample.slnx"), solution);
        var lines = new[]
        {
            Tool(1, "find_references", new JsonObject { ["workspace"] = root, ["id"] = "any" }),
            Tool(2, "find_references", new JsonObject { ["workspace"] = root, ["id"] = "any", ["snapshotId"] = "previous-snapshot" })
        };
        try
        {
            // Act
            var responses = await Talk(solution, lines);

            // Assert
            var missing = Envelope(responses[0], isError: true);
            Assert.Equal("symbol_not_found", missing["error"]!["code"]!.GetValue<string>());
            Assert.Null(missing["freshness"]);
            Assert.Null(missing["coverage"]);
            Assert.False(missing.ContainsKey("pagination"));
            Assert.Equal("snapshot_mismatch", Envelope(responses[1], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.Equal(new[] { "Sample.slnx" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>The sample solution with an interface and two implementations, for a row whose definition is not the
/// queried method, and with a property and a field, for ids that have no references.</summary>
public sealed class McpFindReferencesFixture : IAsyncLifetime
{
    private const string ImplicitUses = """
        using System.Collections;
        using System.Runtime.CompilerServices;

        namespace Sample;
        public sealed class Resource : IDisposable { public void Dispose() { } }
        public sealed class OtherResource : IDisposable { public void Dispose() { } }
        public sealed class AsyncResource : IAsyncDisposable { public ValueTask DisposeAsync() => default; }
        public class Parent : IDisposable { public void Dispose() { } }
        public sealed class Child : Parent { }
        public sealed class Quiet : IDisposable { void IDisposable.Dispose() { } }
        public sealed class Loose { public void Dispose() { } }
        public sealed class Called { public void Dispose() { } }
        public sealed class Both : IDisposable, IAsyncDisposable
        {
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
        public sealed class Numbers
        {
            public Numbers GetEnumerator() => this;
            public bool MoveNext() => false;
            public int Current => 0;
        }
        public struct Ticker : IDisposable
        {
            public bool MoveNext() => false;
            public int Current => 0;
            public void Dispose() { }
        }
        public sealed class Ticks { public Ticker GetEnumerator() => default; }
        public sealed class AsyncNumbers : IAsyncEnumerable<int>
        {
            public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
        public sealed class Pair { public void Deconstruct(out int first, out int second) { first = 1; second = 2; } }
        public sealed class Outer { public void Deconstruct(out int first, out Pair second) { first = 1; second = new(); } }
        public sealed class Later { public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter(); }
        public sealed class Bag : IEnumerable
        {
            public void Add(int value) { }
            public IEnumerator GetEnumerator() => throw new NotSupportedException();
        }
        public sealed class Walker : IEnumerator
        {
            public object Current => 0;
            public bool MoveNext() => false;
            public void Reset() { }
        }
        public abstract class Collected : ICollection<int>
        {
            public abstract int Count { get; }
            public abstract bool IsReadOnly { get; }
            public void Add(int item) { }
            public abstract void Clear();
            public abstract bool Contains(int item);
            public abstract void CopyTo(int[] array, int arrayIndex);
            public abstract bool Remove(int item);
            public abstract IEnumerator<int> GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        public sealed class Words
        {
            public Words Select(Func<int, int> map) { _ = map; return this; }
        }
        public sealed class Chained { public Chained Run() => this; }
        public static class ImplicitUses
        {
            public static Chained Hold(Action run) { run(); return new(); }
            public static void Loop() { foreach (var plain in new Numbers()) { _ = plain; } }
            public static void LoopInAChain() => Hold(() => { foreach (var chained in new Numbers()) { _ = chained; } }).Run();
            public static void LoopWithDispose() { foreach (var tick in new Ticks()) { _ = tick; } }
            public static async Task Wait() { await new Later(); }
            public static int Split() { var (first, second) = new Pair(); return first + second; }
            public static int Assign() { int left, right; (left, right) = new Pair(); return left + right; }
            public static int Nested() { var (head, (near, far)) = new Outer(); return head + near + far; }
            public static int SplitEach(Pair[] pairs)
            {
                var total = 0;
                foreach (var (each, other) in pairs) { total += each + other; }
                return total;
            }
            public static bool Pattern(Pair pair) => pair is Pair(var one, var two) && one == two;
            public static Bag Fill() => new Bag { 5, 6 };
            public static Bag FillFromExpression() { Bag bag = [8]; return bag; }
            public static void ThroughInterfaces(int[] items, List<int> list)
            {
                foreach (var viaArray in items) { _ = viaArray; }
                foreach (var viaList in list) { _ = viaList; }
                _ = new List<int> { 7 };
            }
            public static void Blocks() { using (var block = new Resource()) { _ = block; } }
            public static void Scoped() { using var scoped = new Resource(); _ = scoped; }
            public static void Held() { var held = new Resource(); using (held) { _ = held; } }
            public static void NoUsing() { var plain = new Resource(); _ = plain; }
            public static void Typed() { Resource typed = new Resource(); _ = typed; }
            public static void TwoLocals() { Resource first = new(), second = new(); _ = first; _ = second; }
            public static void AnotherType() { var other = new OtherResource(); _ = other; }
            public static void MetadataType() { var task = Task.CompletedTask; _ = task; }
            public static void ForInitializer() { for (var loop = new Resource(); ;) { _ = loop; break; } }
            public static void TwoInOneUsing() { using (Resource one = new(), two = new()) { _ = one; _ = two; } }
            public static void NoInitializer() { Resource? unset; unset = null; _ = unset; }
            public static void NullInitializer() { Resource nothing = null!; _ = nothing; }
            public static void RefInitializer(Resource[] items) { ref var item = ref items[0]; _ = item; }
            public static void ConvertedLocal() { object converted = new Resource(); _ = converted; }
            public static void ConvertedFirst() { var cast = (object)new Resource(); _ = cast; }
            public static void Inherited() { var derived = new Child(); _ = derived; }
            public static void ExplicitOnly() { var quiet = new Quiet(); using (quiet) { _ = quiet; } }
            public static void NotDisposable() { var loose = new Loose(); _ = loose; }
            public static async Task AsyncScoped() { await using var scoped = new AsyncResource(); _ = scoped; }
            public static async Task AsyncLoop() { await foreach (var number in new AsyncNumbers()) { _ = number; } }
            public static Words Query() => from word in new Words() select word + 1;
        }
        """;

    // No using statement or declaration, so the local has no Dispose row. The directive and the name do not count.
    private const string NoUsing = """
        using System.Text;

        namespace Sample;
        public static class NoUsing
        {
            public static string Run()
            {
                var counted = new Resource();
                new Called().Dispose();
                return new StringBuilder().Append(counted).ToString();
            }
        }
        """;

    // An await using counts for the file, so the plain local has a row. The await using itself has none.
    private const string OnlyAwaitUsing = """
        namespace Sample;
        public static class OnlyAwaitUsing
        {
            public static async Task Run()
            {
                var counted = new Resource();
                _ = counted;
                await using var both = new Both();
                _ = both;
            }
        }
        """;

    // Only a public Dispose() has rows, whatever the use could call, and a type parameter has none. None of these
    // types implements IDisposable, so their rows stay out of the rows of the other Dispose methods.
    private const string DisposeRule = """
        namespace Sample;
        public class OpenDispose { public void Dispose() { } }
        internal sealed class InternalTypeDispose { public void Dispose() { } }
        public sealed class StaticDispose { public static void Dispose() { } }
        public sealed class GenericDispose { public void Dispose<TValue>() { } }
        public sealed class PrivateDispose
        {
            private void Dispose() { }
            public static void Own() { var own = new PrivateDispose(); _ = own; }
        }
        public class ProtectedDispose { protected void Dispose() { } }
        public sealed class ProtectedChild : ProtectedDispose
        {
            public static void Own() { var own = new ProtectedChild(); _ = own; }
        }
        public sealed class InternalDispose { internal void Dispose() { } }
        public class ProtectedInternalDispose { protected internal void Dispose() { } }
        public class PrivateProtectedDispose { private protected void Dispose() { } }
        public sealed class PrivateProtectedChild : PrivateProtectedDispose
        {
            public static void Own() { var own = new PrivateProtectedChild(); _ = own; }
        }
        public static class DisposeRule
        {
            private sealed class Nested { public void Dispose() { } }
            public static void Open() { var open = new OpenDispose(); _ = open; }
            public static void InternalType() { var internalType = new InternalTypeDispose(); _ = internalType; }
            public static void NestedType() { var nested = new Nested(); _ = nested; }
            public static void Static() { var shared = new StaticDispose(); _ = shared; }
            public static void Generic() { var generic = new GenericDispose(); _ = generic; }
            public static void Internal() { var hidden = new InternalDispose(); _ = hidden; }
            public static void ProtectedInternal() { var hidden = new ProtectedInternalDispose(); _ = hidden; }
            public static void ToInterface<TResource>(TResource resource) where TResource : IDisposable, new()
            {
                using (resource) { }
                using var declared = new TResource();
                TResource typed = new TResource();
                var copied = resource;
                _ = typed;
                _ = copied;
            }
            public static void ToClass<TResource>(TResource resource) where TResource : Parent, new()
            {
                using (resource) { }
                using var declared = new TResource();
                TResource typed = new TResource();
                var copied = resource;
                _ = typed;
                _ = copied;
            }
        }
        """;

    private const string Chain = """
        namespace Sample;
        public sealed class Chain
        {
            public static Chain Wrap(Func<int> value) { _ = value; return new(); }
            public static Chain Mark(string name) { _ = name; return new(); }
            public static Chain Hold(Action run) { run(); return new(); }
            public Chain Run() => this;
            public static int Zero() => 0;
            public static void Target() { }
            /// <summary>Named by <see cref="Documented"/>.</summary>
            public static void Documented() { }
            public static void Uses()
            {
                Wrap(Zero).Run();
                Wrap(Zero);
                Wrap(Zero)?.Run();
                Zero();
                Mark(nameof(Target)).Run();
                Mark(nameof(Target));
                Hold(() =>
                {
                    /// <summary>Named by <see cref="Documented"/>.</summary>
                    void Local() { }
                    Local();
                }).Run();
            }
        }
        """;

    // Written with escapes so that each character before a name is plain to read: a tab on line 7, a character in
    // the basic plane on line 8, and a surrogate pair on line 9.
    private const string Columns =
        "namespace Sample;\npublic static class Columns\n{\n    public static void Target() { }\n    public static void Caller()\n    {\n" +
        "\tTarget();\n" +
        "/* \u03b6 */ Target();\n" +
        "/* \ud83d\ude00 */ Target();\n" +
        "    }\n}\n";

    private readonly IndexedSolution isolated = new();

    public string Root => isolated.Root;
    public string IndexPath => isolated.IndexPath;
    public string Solution => Path.Combine(Root, "Sample.slnx");

    public async Task InitializeAsync()
    {
        await isolated.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "Shapes.cs"), """
            namespace Sample;
            public interface IShape { int Area(); }
            public sealed class Square : IShape
            {
                public const int Corners = 4;
                public int Sides { get; set; }
                public int Area() => 1;
                public override string ToString() => "square";
                public static Square operator +(Square left, Square right) => right == left ? left : right;
                public event Action? Changed { add { } remove { } }
            }
            public record Point(int X);
            public static class Shapes
            {
                public static int Direct(Square square) => square.Area();
                public static int Through(IShape shape) => shape.Area();
                public static string Text(object value) => value.ToString() ?? "";
                public static string Number() => 1.ToString();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "ImplicitUses.cs"), ImplicitUses);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "NoUsing.cs"), NoUsing);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "OnlyAwaitUsing.cs"), OnlyAwaitUsing);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "DisposeRule.cs"), DisposeRule);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "Columns.cs"), Columns);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "Chain.cs"), Chain);
        await IndexStore.SaveAsync(await SolutionAnalysis.CreateIndexAsync(Solution));
    }

    public Task DisposeAsync() => isolated.DisposeAsync();
}
