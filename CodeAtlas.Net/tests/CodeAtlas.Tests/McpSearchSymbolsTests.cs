using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>The <c>search_symbols</c> tool over a fixture with authored documentation. Contract: docs/QUERY-CONTRACT.md.</summary>
// Task #04: MCP tool search_symbols
public sealed class McpSearchSymbolsTests(DocumentationCliContractFixture fixture) : IClassFixture<DocumentationCliContractFixture>
{
    private const string Library = "Library/Library.csproj";
    private const string Caller = "Caller/Caller.csproj";
    private const string Describe = "Library/Library.csproj::M:Docs.Methods.Describe(System.Int32)";
    private const string DocumentedWords = "author written description";
    private const string PagedWords = "authored property field";

    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");
    private string Calculator => Path.Combine(fixture.Root, "Library", "Calculator.cs");

    [Fact]
    public async Task WordsInAMethodsDocumentationRankThatMethodFirst()
    {
        // Arrange
        var lines = new[] { Search(1, DocumentedWords) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        AssertShape(found, success: true);
        Assert.Equal("search_symbols", found["command"]!.GetValue<string>());
        Assert.Equal("assisted", found["result"]!["mode"]!.GetValue<string>());
        Assert.Equal("lexical-candidates", found["result"]!["decision"]!.GetValue<string>());
        var first = found["result"]!["symbols"]![0]!;
        Assert.Equal(Describe, first["symbol"]!["id"]!.GetValue<string>());
        Assert.Contains(first["reasons"]!.AsArray(), reason => reason!["field"]!.GetValue<string>() == "developer-documentation");
    }

    [Fact]
    public async Task NamesModeIgnoresDocumentation()
    {
        // Arrange
        var lines = new[] { Search(1, DocumentedWords, mode: "names") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.Empty(found["result"]!["symbols"]!.AsArray());
        Assert.Equal("names", found["result"]!["mode"]!.GetValue<string>());
    }

    // One word found only in documentation is below the evidence gate, as it is for the CLI.
    [Fact]
    public async Task ASingleDocumentationWordIsWithheldNotGuessed()
    {
        // Arrange
        var lines = new[] { Search(1, "written") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        Assert.Empty(found["result"]!["symbols"]!.AsArray());
        Assert.Equal("abstained-insufficient-evidence", found["result"]!["decision"]!.GetValue<string>());
        Assert.True(found["result"]!["withheldWeakMatches"]!.GetValue<int>() >= 1);
    }

    [Theory]
    [InlineData(DocumentedWords, null, null, null, null)]
    [InlineData(DocumentedWords, "assisted", "all", null, null)]
    [InlineData(DocumentedWords, "assisted", "method", Library, 1)]
    [InlineData(PagedWords, "assisted", "all", null, 2)]
    [InlineData("authored type", "assisted", "all", null, 1)]
    [InlineData("entry", "names", "all", null, null)]
    [InlineData("LocalCaller", "names", "method", null, null)]
    [InlineData(DocumentedWords, "assisted", "method", Caller, null)]
    [InlineData("Calculate", "names", "method", null, null)]
    [InlineData("angular component", null, null, null, null)]
    public async Task RowsAndSearchFieldsMatchTheCli(string query, string? mode, string? kind, string? project, int? limit)
    {
        // Arrange
        var cli = await RunCli(CliArguments(query, mode, kind, project, limit));
        Assert.Equal(0, cli.Exit);
        var lines = new[] { Search(1, query, mode, kind, project, limit: limit) };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var found = Envelope(responses[0], isError: false);
        var result = found["result"]!.AsObject();
        foreach (var field in new[] { "symbols", "rankingVersion", "mode", "language", "queryTokens", "unmatchedQueryTokens",
            "evidencePolicy", "decision", "withheldWeakMatches", "limitations" })
            Assert.True(JsonNode.DeepEquals(cli.Json[field], result[field]), field);
        Assert.True(JsonNode.DeepEquals(cli.Json["searchCoverage"], result["searchCoverage"]));
        Assert.True(JsonNode.DeepEquals(cli.Json["pagination"], found["pagination"]));
        Assert.Equal(cli.Json["warningCount"]!.GetValue<int>(), found["warningCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task KindAndProjectFiltersKeepOnlyMatchingRows()
    {
        // Arrange
        var lines = new[]
        {
            Search(1, PagedWords, kind: "all"),
            Search(2, "authored property", kind: "property"),
            Search(3, PagedWords, kind: "type"),
            Search(4, DocumentedWords, project: Library),
            Search(5, DocumentedWords, project: Caller)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var all = Rows(responses[0]);
        Assert.Equal(new[] { "field", "property", "type" }, all.Select(row => row["symbol"]!["kind"]!.GetValue<string>()).Order());
        var properties = Rows(responses[1]);
        Assert.NotEmpty(properties);
        Assert.All(properties, row => Assert.Equal("property", row["symbol"]!["kind"]!.GetValue<string>()));
        Assert.Equal("type", Assert.Single(Rows(responses[2]))["symbol"]!["kind"]!.GetValue<string>());
        var library = Rows(responses[3]);
        Assert.NotEmpty(library);
        Assert.All(library, row => Assert.Equal(Library, row["symbol"]!["project"]!.GetValue<string>()));
        var caller = Envelope(responses[4], isError: false);
        Assert.Empty(caller["result"]!["symbols"]!.AsArray());
        Assert.Equal(0, caller["pagination"]!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task PagesContinueWithTheReturnedSnapshotId()
    {
        // Arrange
        var generation = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).Index.IndexId;
        var lines = new[]
        {
            Search(1, PagedWords, kind: "all"),
            Search(2, PagedWords, kind: "all", limit: 1),
            Search(3, PagedWords, kind: "all", offset: 1, limit: 1, snapshotId: generation),
            Search(4, PagedWords, kind: "all", offset: 1000, snapshotId: generation)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var whole = Rows(responses[0]);
        Assert.True(whole.Length >= 2);
        var first = Envelope(responses[1], isError: false);
        Assert.Equal(generation, first["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.True(first["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(1, first["pagination"]!["nextOffset"]!.GetValue<int>());
        Assert.True(first["pagination"]!["continuationSupported"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(whole[0], first["result"]!["symbols"]![0]));
        var second = Envelope(responses[2], isError: false);
        Assert.Equal(1, second["pagination"]!["offset"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(whole[1], second["result"]!["symbols"]![0]));
        var past = Envelope(responses[3], isError: false);
        Assert.Empty(past["result"]!["symbols"]!.AsArray());
        Assert.Equal(whole.Length, past["pagination"]!["total"]!.GetValue<int>());
        Assert.False(past["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(past["pagination"]!["nextOffset"]);
    }

    [Fact]
    public async Task ASnapshotIdFromAReplacedGenerationIsRefusedAndAFreshQueryGetsTheNewOne()
    {
        // Arrange
        var previous = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).Index.IndexId;
        var lines = new[]
        {
            Search(1, DocumentedWords, snapshotId: previous),
            Tool(2, "index", new JsonObject { ["workspace"] = fixture.Root }),
            Search(3, DocumentedWords, snapshotId: previous),
            Search(4, DocumentedWords, offset: 1, snapshotId: previous),
            Search(5, DocumentedWords)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Envelope(responses[0], isError: false);
        var published = Envelope(responses[1], isError: false)["result"]!["generationId"]!.GetValue<string>();
        Assert.NotEqual(previous, published);
        var mismatch = Envelope(responses[2], isError: true);
        Assert.Equal("search_symbols", mismatch["command"]!.GetValue<string>());
        Assert.Equal("snapshot_mismatch", mismatch["error"]!["code"]!.GetValue<string>());
        Assert.Equal("The index was replaced. Start a new query at offset zero.", mismatch["error"]!["message"]!.GetValue<string>());
        Assert.Null(mismatch["freshness"]);
        Assert.Null(mismatch["coverage"]);
        Assert.False(mismatch.ContainsKey("pagination"));
        Assert.False(mismatch.ContainsKey("result"));
        Assert.Equal("snapshot_mismatch", Envelope(responses[3], isError: true)["error"]!["code"]!.GetValue<string>());
        Assert.Equal(published, Envelope(responses[4], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnEditedInputIsRefusedAsStaleWithoutStoppingTheServer()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        var lines = new[] { Search(1, DocumentedWords), Search(2, DocumentedWords), Request(3, "tools/list") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.AppendAllText(Calculator, Environment.NewLine + "// changed");
            });

            // Assert
            Envelope(responses[0], isError: false);
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("search_symbols", stale["command"]!.GetValue<string>());
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The saved index is stale.", stale["error"]!["message"]!.GetValue<string>());
            Assert.True(stale["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Contains("Library/Calculator.cs", stale["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
            Assert.NotNull(stale["coverage"]);
            Assert.False(stale.ContainsKey("pagination"));
            Assert.False(stale.ContainsKey("result"));
            Assert.Contains("search_symbols", ToolNames(responses[2]));
        }
        finally
        {
            await File.WriteAllTextAsync(Calculator, original);
        }
    }

    [Fact]
    public async Task AReplacedSnapshotIdIsReportedBeforeStaleness()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        var lines = new[] { Search(1, DocumentedWords, snapshotId: "previous-snapshot") };
        await File.AppendAllTextAsync(Calculator, Environment.NewLine + "// changed");
        try
        {
            // Act
            var responses = await Talk(Solution, lines);

            // Assert
            Assert.Equal("snapshot_mismatch", Envelope(responses[0], isError: true)["error"]!["code"]!.GetValue<string>());
        }
        finally
        {
            await File.WriteAllTextAsync(Calculator, original);
        }
    }

    [Fact]
    public async Task AChangedInputThatCannotBeReadIsRefusedAsAnIoFailure()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        FileStream? held = null;
        var lines = new[] { Search(1, DocumentedWords), Search(2, DocumentedWords) };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index != 1) return;
                File.AppendAllText(Calculator, "// changed");
                held = new FileStream(Calculator, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var failed = Envelope(responses[1], isError: true);
            Assert.Equal("search_symbols", failed["command"]!.GetValue<string>());
            Assert.Equal("snapshot_io_failed", failed["error"]!["code"]!.GetValue<string>());
            Assert.NotNull(failed["freshness"]);
            Assert.NotNull(failed["coverage"]);
        }
        finally
        {
            held?.Dispose();
            await File.WriteAllTextAsync(Calculator, original);
        }
    }

    [Theory]
    [InlineData("workspace", null, "workspace is required.")]
    [InlineData("workspace", "7", "workspace is required.")]
    [InlineData("workspace", "\"\"", "workspace must not be empty.")]
    [InlineData("workspace", "\"elsewhere\"", "workspace must be the solution directory.")]
    [InlineData("query", null, "query is required.")]
    [InlineData("query", "7", "query is required.")]
    [InlineData("query", "\"\"", "query must not be empty.")]
    [InlineData("query", "\"   \"", "query must have 1-4096 characters and at least one searchable token.")]
    [InlineData("query", "\"the\"", "query must have 1-4096 characters and at least one searchable token.")]
    [InlineData("mode", "\"\"", "mode must not be empty.")]
    [InlineData("mode", "7", "mode must not be empty.")]
    [InlineData("mode", "\"fuzzy\"", "mode must be names or assisted.")]
    [InlineData("kind", "\"\"", "kind must not be empty.")]
    [InlineData("kind", "\"component\"", "Unknown symbol kind.")]
    [InlineData("project", "\"\"", "project must not be empty.")]
    [InlineData("snapshotId", "\"\"", "snapshotId must not be empty.")]
    [InlineData("offset", "-1", "offset must be a nonnegative integer.")]
    [InlineData("offset", "1.5", "offset must be a nonnegative integer.")]
    [InlineData("offset", "\"1\"", "offset must be a nonnegative integer.")]
    [InlineData("offset", "1", "offset greater than zero requires snapshotId.")]
    [InlineData("limit", "0", "limit must be an integer from 1 through 1000.")]
    [InlineData("limit", "1001", "limit must be an integer from 1 through 1000.")]
    public async Task EachInvalidArgumentReturnsItsDocumentedMessage(string field, string? value, string message)
    {
        // Arrange
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["query"] = DocumentedWords };
        arguments.Remove(field);
        if (value is not null) arguments[field] = JsonNode.Parse(value);
        var lines = new[] { Tool(1, "search_symbols", arguments), Request(2, "tools/list") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        AssertInvalid(responses[0], message);
        Assert.Contains("search_symbols", ToolNames(responses[1]));
    }

    [Fact]
    public async Task AQueryOverTheLengthLimitReturnsItsDocumentedMessage()
    {
        // Arrange
        var longest = Search(1, new string('a', PurposeSearch.MaximumQueryCharacters));
        var tooLong = Search(2, new string('a', PurposeSearch.MaximumQueryCharacters + 1));

        // Act
        var responses = await Talk(Solution, [longest, tooLong]);

        // Assert
        Envelope(responses[0], isError: false);
        AssertInvalid(responses[1], "query must have 1-4096 characters and at least one searchable token.");
    }

    [Fact]
    public async Task ToolsListDescribesTheTool()
    {
        // Arrange
        var lines = new[] { Request(1, "tools/list") };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.Equal(new[] { "find_symbol", "search_symbols", "index_status", "coverage", "index" }, ToolNames(responses[0]));
        var tool = responses[0]["result"]!["tools"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "search_symbols")!;
        Assert.Equal(new[] { "workspace", "query" }, tool["inputSchema"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(new[] { "workspace", "query", "mode", "kind", "project", "offset", "limit", "snapshotId" },
            tool["inputSchema"]!["properties"]!.AsObject().Select(property => property.Key));
        Assert.Equal("integer", tool["inputSchema"]!["properties"]!["limit"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASecondSearchIsServedWarmWithoutHashingUnchangedInputsAgain()
    {
        // Arrange
        FileStream? held = null;
        SnapshotInputs.ResetFullFreshnessChecks(Solution);
        var lines = new[] { Search(1, DocumentedWords), Search(2, DocumentedWords, mode: "names") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                // A hash of this file would now fail, so a successful answer proves it was not read.
                if (index == 1) held = new FileStream(Calculator, FileMode.Open, FileAccess.Read, FileShare.None);
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

    private string Search(int id, string query, string? mode = null, string? kind = null, string? project = null,
        int? offset = null, int? limit = null, string? snapshotId = null)
    {
        var arguments = new JsonObject { ["workspace"] = fixture.Root, ["query"] = query };
        if (mode is not null) arguments["mode"] = mode;
        if (kind is not null) arguments["kind"] = kind;
        if (project is not null) arguments["project"] = project;
        if (offset is not null) arguments["offset"] = offset;
        if (limit is not null) arguments["limit"] = limit;
        if (snapshotId is not null) arguments["snapshotId"] = snapshotId;
        return Tool(id, "search_symbols", arguments);
    }

    private string[] CliArguments(string query, string? mode, string? kind, string? project, int? limit)
    {
        var arguments = new List<string> { "search-symbols", "--index", fixture.IndexPath, "--query", query };
        if (mode is not null) arguments.AddRange(["--mode", mode]);
        if (kind is not null) arguments.AddRange(["--kind", kind]);
        if (project is not null) arguments.AddRange(["--project", project]);
        if (limit is not null) arguments.AddRange(["--limit", limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return [.. arguments];
    }

    private static JsonNode[] Rows(JsonObject response) =>
        Envelope(response, isError: false)["result"]!["symbols"]!.AsArray().Select(row => row!).ToArray();

    private static string[] ToolNames(JsonObject response) =>
        response["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

    private static void AssertInvalid(JsonObject response, string message)
    {
        var invalid = Envelope(response, isError: true);
        Assert.Equal("search_symbols", invalid["command"]!.GetValue<string>());
        Assert.Equal("invalid_arguments", invalid["error"]!["code"]!.GetValue<string>());
        Assert.Equal(message, invalid["error"]!["message"]!.GetValue<string>());
        Assert.Null(invalid["freshness"]);
        Assert.Null(invalid["coverage"]);
        Assert.False(invalid.ContainsKey("pagination"));
        Assert.False(invalid.ContainsKey("result"));
    }
}

public sealed class McpSearchNoIndexTests
{
    [Fact]
    public async Task WithNoPublishedIndexItSucceedsEmptyUnlessASnapshotIdWasSupplied()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("codeatlas-mcp-search-").FullName;
        var solution = Path.Combine(root, "Sample.slnx");
        File.Copy(Path.Combine(Repository(), "tests", "Fixtures", "Sample.slnx"), solution);
        var lines = new[]
        {
            Tool(1, "search_symbols", new JsonObject { ["workspace"] = root, ["query"] = "price rules", ["limit"] = 5 }),
            Tool(2, "search_symbols", new JsonObject { ["workspace"] = root, ["query"] = "price rules", ["snapshotId"] = "previous-snapshot" })
        };
        try
        {
            // Act
            var responses = await Talk(solution, lines);

            // Assert
            var missing = Envelope(responses[0], isError: false);
            AssertShape(missing, success: true);
            Assert.Empty(missing["result"]!["symbols"]!.AsArray());
            Assert.Equal("abstained-no-index", missing["result"]!["decision"]!.GetValue<string>());
            Assert.Equal("assisted", missing["result"]!["mode"]!.GetValue<string>());
            Assert.Equal("no-index", missing["freshness"]!["inputCoverage"]!["scope"]!.GetValue<string>());
            Assert.Equal(new[] { "no-index" }, missing["coverage"]!["limitations"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal(5, missing["pagination"]!["limit"]!.GetValue<int>());
            Assert.Null(missing["pagination"]!["snapshotId"]);
            Assert.False(missing["pagination"]!["continuationSupported"]!.GetValue<bool>());
            Assert.Equal("snapshot_mismatch", Envelope(responses[1], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.Equal(new[] { "Sample.slnx" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
