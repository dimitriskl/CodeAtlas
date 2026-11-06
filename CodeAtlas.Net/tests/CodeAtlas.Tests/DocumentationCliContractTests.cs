using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DocumentationCliContractTests(DocumentationCliContractFixture fixture)
    : IClassFixture<DocumentationCliContractFixture>
{
    private const string Project = "Library/Library.csproj";
    private const string DescribeId = Project + "::M:Docs.Methods.Describe(System.Int32)";
    private const string MissingId = Project + "::M:Docs.Methods.Missing";
    private const string EntryMethodId = Project + "::M:Docs.Methods.Entry(System.Int32)";
    private static readonly string[] SummaryKeys =
        ["containingType", "documentation", "id", "kind", "location", "name", "namespace", "project", "signature", "typeKind"];

    [Fact]
    public async Task DefaultAndExplicitMethodKindPreserveTheExactExistingMethodShapeAndPaging()
    {
        // Arrange
        var expectedIds = fixture.Index.Methods.Where(method => method.Name == "Calculate")
            .OrderBy(method => method.Id, StringComparer.Ordinal).Select(method => method.Id).ToArray();
        string[] expectedKeys = ["containingType", "id", "location", "name", "project", "signature"];

        // Act
        var first = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--limit", "1");
        var explicitFirst = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--kind", "method", "--limit", "1");
        var second = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--kind", "method",
            "--limit", "1", "--offset", "1", "--snapshot", fixture.Index.IndexId);

        // Assert
        AssertSuccess(first, "find-symbol");
        AssertSuccess(explicitFirst, "find-symbol");
        AssertSuccess(second, "find-symbol");
        Assert.True(JsonNode.DeepEquals(first.Json["symbols"], explicitFirst.Json["symbols"]));
        Assert.True(JsonNode.DeepEquals(first.Json["pagination"], explicitFirst.Json["pagination"]));
        var symbols = first.Json["symbols"]!.AsArray().Concat(second.Json["symbols"]!.AsArray()).ToArray();
        Assert.Equal(expectedIds, symbols.Select(symbol => symbol!["id"]!.GetValue<string>()));
        Assert.All(symbols, symbol => Assert.Equal(expectedKeys, symbol!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal)));
        Assert.True(first.Json["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.False(second.Json["pagination"]!["hasMore"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("type", 2)]
    [InlineData("property", 1)]
    [InlineData("field", 1)]
    [InlineData("all", 5)]
    public async Task CatalogKindsReturnOneCompactUniformProjection(string kind, int expectedCount)
    {
        // Arrange
        const string name = "Entry";

        // Act
        var result = await Run("find-symbol", "--index", fixture.IndexPath, "--name", name, "--kind", kind);

        // Assert
        AssertSuccess(result, "find-symbol");
        var symbols = result.Json["symbols"]!.AsArray();
        Assert.Equal(expectedCount, symbols.Count);
        Assert.Equal(expectedCount, result.Json["pagination"]!["total"]!.GetValue<int>());
        Assert.All(symbols, symbol =>
        {
            Assert.Equal(SummaryKeys, symbol!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal(name, symbol["name"]!.GetValue<string>());
            Assert.Equal(Project, symbol["project"]!.GetValue<string>());
            Assert.Equal("Docs", symbol["namespace"]!.GetValue<string>());
            Assert.Contains(symbol["kind"]!.GetValue<string>(), kind == "all"
                ? new[] { "type", "method", "property", "field" } : new[] { kind });
            Assert.NotNull(symbol["location"]);
            Assert.Equal(new[] { "origin", "status", "summary", "summaryTruncated" },
                symbol["documentation"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal("present", symbol["documentation"]!["status"]!.GetValue<string>());
            Assert.Equal("developer-xml", symbol["documentation"]!["origin"]!.GetValue<string>());
            Assert.InRange(symbol["documentation"]!["summary"]!.GetValue<string>().Length, 1, 1000);
        });
        Assert.DoesNotContain("rawComment", result.Stdout);
        Assert.DoesNotContain("parsedXml", result.Stdout);
        Assert.DoesNotContain("sourceSymbolId", result.Stdout);
    }

    [Fact]
    public async Task AllKindIncludesMethodsWithBoundedAuthorPreviewAndActualDeclaringTypes()
    {
        // Arrange
        const string nestedType = Project + "::T:Docs.Container.Entry";
        const string topLevelType = Project + "::T:Docs.Entry";

        // Act
        var all = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all");
        var nested = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "type",
            "--type", "Docs.Container", "--project", Project);
        var ownNameFilter = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "type", "--type", "Docs.Entry");

        // Assert
        AssertSuccess(all, "find-symbol");
        AssertSuccess(nested, "find-symbol");
        AssertSuccess(ownNameFilter, "find-symbol");
        var symbols = all.Json["symbols"]!.AsArray();
        var method = Assert.Single(symbols, symbol => symbol!["id"]!.GetValue<string>() == EntryMethodId)!;
        Assert.Equal("method", method["kind"]!.GetValue<string>());
        Assert.Equal(SummaryKeys, method.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1000, method["documentation"]!["summary"]!.GetValue<string>().Length);
        Assert.True(method["documentation"]!["summaryTruncated"]!.GetValue<bool>());
        Assert.StartsWith("Author supplied ", method["documentation"]!["summary"]!.GetValue<string>());
        Assert.Null(Assert.Single(symbols, symbol => symbol!["id"]!.GetValue<string>() == topLevelType)!["containingType"]);
        Assert.Equal(nestedType, Assert.Single(nested.Json["symbols"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Empty(ownNameFilter.Json["symbols"]!.AsArray());
    }

    [Fact]
    public async Task GetDocumentationReturnsFullAuthoredXmlAndEvidenceRatherThanOnlyThePreview()
    {
        // Arrange
        const string wording = "Author-written description.";

        // Act
        var result = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", DescribeId, "--profile");

        // Assert
        AssertSuccess(result, "get-documentation");
        Assert.Equal(DescribeId, result.Json["symbol"]!["id"]!.GetValue<string>());
        var documentation = result.Json["symbol"]!["documentation"]!;
        Assert.Equal("present", documentation["status"]!.GetValue<string>());
        var comment = Assert.Single(documentation["comments"]!.AsArray())!;
        Assert.Contains("/// <summary>" + wording, comment["rawComment"]!.GetValue<string>());
        Assert.Contains("<see", comment["parsedXml"]!.GetValue<string>());
        var summary = Assert.Single(documentation["sections"]!.AsArray(), section => section!["tag"]!.GetValue<string>() == "summary")!;
        Assert.Equal(wording, summary["text"]!.GetValue<string>().Trim());
        Assert.Contains("<see", summary["xml"]!.GetValue<string>());
        Assert.Equal("developer-xml", summary["evidence"]!["origin"]!.GetValue<string>());
        Assert.Equal(DescribeId, summary["evidence"]!["sourceSymbolId"]!.GetValue<string>());
        Assert.Equal(comment["id"]!.GetValue<string>(), summary["evidence"]!["commentId"]!.GetValue<string>());
        Assert.Equal("Library/DocumentationContract.cs", summary["evidence"]!["location"]!["start"]!["file"]!.GetValue<string>());
        Assert.True(summary["evidence"]!["location"]!["start"]!["line"]!.GetValue<int>() > 0);
        Assert.Empty(summary["evidence"]!["inheritedVia"]!.AsArray());
        Assert.Contains(documentation["sections"]!.AsArray(), section => section!["tag"]!.GetValue<string>() == "param"
            && section["name"]!.GetValue<string>() == "input" && section["text"]!.GetValue<string>() == "Author input.");
        Assert.NotNull(result.Json["queryTimings"]);
        Assert.Null(result.Json["pagination"]);
    }

    [Fact]
    public async Task AnUndocumentedSymbolIsSuccessfulAndDistinctFromAnUnknownId()
    {
        // Arrange
        const string unknown = Project + "::M:Docs.Methods.DoesNotExist";

        // Act
        var missing = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", MissingId);
        var absent = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", unknown);

        // Assert
        AssertSuccess(missing, "get-documentation");
        Assert.Equal(MissingId, missing.Json["symbol"]!["id"]!.GetValue<string>());
        Assert.Equal("missing", missing.Json["symbol"]!["documentation"]!["status"]!.GetValue<string>());
        Assert.Empty(missing.Json["symbol"]!["documentation"]!["comments"]!.AsArray());
        Assert.Empty(missing.Json["symbol"]!["documentation"]!["sections"]!.AsArray());
        AssertError(absent, "get-documentation", 1, "symbol_not_found");
        Assert.Null(absent.Json["symbol"]);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return [new[] { "find-symbol", "--index", "missing.json", "--name", "Entry", "--kind", "constructor" }];
        yield return [new[] { "find-symbol", "--index", "missing.json", "--name", "Entry", "--kind", "TYPE" }];
        yield return [new[] { "find-symbol", "--index", "missing.json", "--name", "Entry", "--kind", "all", "--kind", "type" }];
        yield return [new[] { "find-symbol", "--index", "missing.json", "--name", "Entry", "--kind", "all", "--limit", "1001" }];
        yield return [new[] { "find-symbol", "--index", "missing.json", "--name", "Entry", "--kind", "all", "--offset", "1" }];
        yield return [new[] { "find-symbol", "--solution", "missing.slnx", "--name", "Entry", "--kind", "all", "--snapshot", "old" }];
        yield return [new[] { "find-references", "--index", "missing.json", "--symbol", DescribeId, "--kind", "method" }];
        foreach (var option in new[] { "--project", "--type", "--name", "--kind", "--limit", "--offset" })
            yield return [new[] { "get-documentation", "--index", "missing.json", "--symbol", DescribeId, option, "1" }];
        yield return [new[] { "get-documentation", "--index", "missing.json", "--symbol", DescribeId, "--symbol", MissingId }];
        yield return [new[] { "get-documentation", "--index", "missing.json", "--solution", "missing.slnx", "--symbol", DescribeId }];
        yield return [new[] { "get-documentation", "--solution", "missing.slnx", "--symbol", DescribeId, "--snapshot", "old" }];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidKindsAndSingletonOptionsAreRejectedBeforeOpeningInputs(string[] arguments)
    {
        // Arrange
        var command = arguments[0];

        // Act
        var result = await Run(arguments);

        // Assert
        AssertError(result, command, 2, "invalid_arguments");
        Assert.Null(result.Json["indexId"]);
        Assert.Null(result.Json["freshness"]);
    }

    [Fact]
    public async Task SchemaOneRetainsMethodPagesAndReferencesButNewCatalogFeaturesRequireRebuild()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, ".codeatlas", "documentation-schema-one.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(fixture.IndexArtifactPath))!.AsObject();
        index["schemaVersion"] = 1;
        index.Remove("symbols");
        index.Remove("documentationCoverage");
        await File.WriteAllTextAsync(path, index.ToJsonString());
        var method = fixture.Index.Methods.Single(item => item.Id.EndsWith("Calculate(System.Int32)", StringComparison.Ordinal));
        try
        {
            // Act
            var ordinary = await Run("find-symbol", "--index", path, "--name", "Calculate", "--limit", "1");
            var continuation = await Run("find-symbol", "--index", path, "--name", "Calculate", "--kind", "method",
                "--limit", "1", "--offset", "1", "--snapshot", fixture.Index.IndexId);
            var references = await Run("find-references", "--index", path, "--symbol", method.Id);
            var documentation = await Run("get-documentation", "--index", path, "--symbol", DescribeId);
            var catalogResults = new List<Result>();
            foreach (var kind in new[] { "type", "property", "field", "all" })
                catalogResults.Add(await Run("find-symbol", "--index", path, "--name", "Entry", "--kind", kind));

            // Assert
            AssertSuccess(ordinary, "find-symbol");
            AssertSuccess(continuation, "find-symbol");
            AssertSuccess(references, "find-references");
            Assert.Equal("legacy-unverified", ordinary.Json["snapshotIntegrity"]!.GetValue<string>());
            Assert.Equal(2, ordinary.Json["pagination"]!["total"]!.GetValue<int>());
            Assert.NotEqual(Assert.Single(ordinary.Json["symbols"]!.AsArray())!["id"]!.GetValue<string>(),
                Assert.Single(continuation.Json["symbols"]!.AsArray())!["id"]!.GetValue<string>());
            Assert.Equal(fixture.Index.References[method.Id].Length, references.Json["references"]!.AsArray().Count);
            AssertError(documentation, "get-documentation", 1, "rebuild_required");
            Assert.All(catalogResults, result => AssertError(result, "find-symbol", 1, "rebuild_required"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("T:Docs.Entry")]
    [InlineData("P:Docs.Catalog.Entry")]
    [InlineData("F:Docs.Fields.Entry")]
    public async Task KnownNonMethodReferenceRequestsHaveAnExplicitUnsupportedKindError(string declaration)
    {
        // Arrange
        var id = Project + "::" + declaration;

        // Act
        var result = await Run("find-references", "--index", fixture.IndexPath, "--symbol", id);

        // Assert
        AssertError(result, "find-references", 1, "unsupported_symbol_kind");
        Assert.Null(result.Json["references"]);
    }

    [Fact]
    public async Task AllKindPaginationIsDeterministicAndDocumentationHonorsTheSameSnapshotGuard()
    {
        // Arrange
        var expected = fixture.Index.Symbols!.Where(symbol => symbol.Name == "Entry")
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal).Select(symbol => symbol.Id).ToArray();
        var snapshot = fixture.Index.IndexId;

        // Act
        var first = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all", "--limit", "2");
        var second = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all", "--limit", "2", "--offset", "2", "--snapshot", snapshot);
        var third = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all", "--limit", "2", "--offset", "4", "--snapshot", snapshot);
        var wrongPage = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all", "--offset", "2", "--snapshot", "previous");
        var wrongDocument = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", DescribeId, "--snapshot", "previous");
        var document = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", DescribeId, "--snapshot", snapshot);

        // Assert
        Assert.All(new[] { first, second, third }, result => AssertSuccess(result, "find-symbol"));
        AssertSuccess(document, "get-documentation");
        Assert.Equal(expected, new[] { first, second, third }.SelectMany(result => result.Json["symbols"]!.AsArray())
            .Select(symbol => symbol!["id"]!.GetValue<string>()));
        Assert.Equal(2, first.Json["pagination"]!["nextOffset"]!.GetValue<int>());
        Assert.Equal(4, second.Json["pagination"]!["nextOffset"]!.GetValue<int>());
        Assert.False(third.Json["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(third.Json["pagination"]!["nextOffset"]);
        AssertError(wrongPage, "find-symbol", 3, "snapshot_mismatch");
        AssertError(wrongDocument, "get-documentation", 3, "snapshot_mismatch");
        Assert.Equal(snapshot, wrongDocument.Json["indexId"]!.GetValue<string>());
        Assert.Null(wrongDocument.Json["symbol"]);
    }

    [Fact]
    public async Task SameLengthDocumentationEditInvalidatesBothSavedCatalogAndFullDocumentation()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, "Library", "DocumentationContract.cs");
        var original = await File.ReadAllTextAsync(path);
        var changed = original.Replace("Author-written description.", "Revised author description.", StringComparison.Ordinal);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, changed);
        File.SetLastWriteTimeUtc(path, timestamp);
        try
        {
            // Act
            var documentation = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", DescribeId);
            var catalog = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all");

            // Assert
            Assert.Equal(original.Length, changed.Length);
            AssertError(documentation, "get-documentation", 3, "stale_index");
            AssertError(catalog, "find-symbol", 3, "stale_index");
            Assert.True(documentation.Json["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Contains("Library/DocumentationContract.cs", documentation.Json["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
            Assert.Null(documentation.Json["symbol"]);
            Assert.Null(catalog.Json["symbols"]);
        }
        finally
        {
            await File.WriteAllTextAsync(path, original);
            File.SetLastWriteTimeUtc(path, timestamp);
        }
    }

    [Fact]
    public async Task SavedQueriesLeaveAllArtifactsUntouchedAndMatchLiveAuthorData()
    {
        // Arrange
        var storage = Path.Combine(fixture.Root, ".codeatlas");
        var before = Directory.GetFiles(storage, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        var directories = Directory.GetDirectories(storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        // Act
        var savedCatalog = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Entry", "--kind", "all");
        var savedDocument = await Run("get-documentation", "--index", fixture.IndexPath, "--symbol", DescribeId);
        var afterFiles = Directory.GetFiles(storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var after = afterFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        var afterDirectories = Directory.GetDirectories(storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var liveCatalog = await Run("find-symbol", "--solution", fixture.Index.Solution, "--name", "Entry", "--kind", "all");
        var liveDocument = await Run("get-documentation", "--solution", fixture.Index.Solution, "--symbol", DescribeId);

        // Assert
        AssertSuccess(savedCatalog, "find-symbol");
        AssertSuccess(savedDocument, "get-documentation");
        AssertSuccess(liveCatalog, "find-symbol");
        AssertSuccess(liveDocument, "get-documentation");
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), afterFiles);
        Assert.Equal(directories, afterDirectories);
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, after[path].Bytes);
            Assert.Equal(value.Modified, after[path].Modified);
        }
        Assert.True(JsonNode.DeepEquals(savedCatalog.Json["symbols"], liveCatalog.Json["symbols"]));
        Assert.True(JsonNode.DeepEquals(savedDocument.Json["symbol"], liveDocument.Json["symbol"]));
        Assert.Equal("live-analysis", liveDocument.Json["freshness"]!["state"]!.GetValue<string>());
        Assert.False(liveCatalog.Json["pagination"]!["continuationSupported"]!.GetValue<bool>());
        Assert.Null(liveCatalog.Json["pagination"]!["snapshotId"]);
    }

    private static void AssertSuccess(Result result, string command)
    {
        Assert.True(result.Exit == 0, result.Stderr);
        Assert.Empty(result.Stderr);
        AssertEnvelope(result, command, true);
    }

    private static void AssertError(Result result, string command, int exit, string code)
    {
        Assert.Equal(exit, result.Exit);
        Assert.Empty(result.Stdout);
        AssertEnvelope(result, command, false);
        Assert.Equal(code, result.Json["error"]!["code"]!.GetValue<string>());
    }

    private static void AssertEnvelope(Result result, string command, bool success)
    {
        Assert.Equal(1, result.Json["contractVersion"]!.GetValue<int>());
        Assert.Equal(command, result.Json["command"]!.GetValue<string>());
        Assert.Equal(success, result.Json["success"]!.GetValue<bool>());
        Assert.Equal(CodeAtlasVersion.Current, result.Json["responseToolVersion"]!.GetValue<string>());
        Assert.True(result.Json.ContainsKey("freshness"));
        Assert.True(result.Json.ContainsKey("coverage"));
    }

    private sealed record Result(int Exit, string Stdout, string Stderr)
    {
        public JsonObject Json => JsonNode.Parse(Exit == 0 ? Stdout : Stderr)!.AsObject();
    }

    private static async Task<Result> Run(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Required checkout not found.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = await ChildProcess.RunAsync(start);
        return new(process.ExitCode, process.Output, process.Error);
    }
}

public sealed class DocumentationCliContractFixture : IAsyncLifetime
{
    private readonly IndexedSolution isolated = new();
    public string Root => isolated.Root;
    public string IndexPath => isolated.IndexPath;
    public string IndexArtifactPath => isolated.IndexArtifactPath;
    public CodebaseIndex Index { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await isolated.InitializeAsync();
        var longSummary = "Author supplied " + new string('x', 1100);
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "DocumentationContract.cs"), $$"""
            namespace Docs;
            /// <summary>Authored top-level type.</summary>
            public class Entry { }
            public class Container
            {
                /// <summary>Authored nested type.</summary>
                public class Entry { }
            }
            public class Catalog
            {
                /// <summary>Authored property.</summary>
                public int Entry { get; set; }
            }
            public class Fields
            {
                /// <summary>Authored field.</summary>
                public const int Entry = 1;
            }
            public class Methods
            {
                /// <summary>{{longSummary}}</summary>
                public int Entry(int input) => input;
                /// <summary>Author-written description.<see cref="Entry(int)"/></summary>
                /// <param name="input">Author input.</param>
                /// <remarks>Preserve this full XML section.</remarks>
                public int Describe(int input) => Entry(input);
                public void Missing() { }
            }
            """);
        Index = await SolutionAnalysis.CreateIndexAsync(isolated.Index.Solution);
        await IndexStore.SaveAsync(Index);
    }

    public Task DisposeAsync() => isolated.DisposeAsync();
}
