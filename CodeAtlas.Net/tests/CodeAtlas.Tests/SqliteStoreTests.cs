using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas.Explanations;
using CodeAtlas.Persistence;
using CodeAtlas.Storage;
using Xunit;

namespace CodeAtlas.Tests;

/// <summary>SQLite store: round trip, per-table row queries, ranking, paging and CLI equality with the JSON store.</summary>
public sealed class SqliteStoreTests(SemanticSolution semantic, DocumentationCliContractFixture documented)
    : IClassFixture<SemanticSolution>, IClassFixture<DocumentationCliContractFixture>, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-sqlite-store-");

    public void Dispose()
    {
        try { root.Delete(recursive: true); }
        catch (IOException) { }
    }

    private CodebaseIndex Fixture(string name) => name == "semantic" ? semantic.Index : documented.Index;

    private string Write(CodebaseIndex index)
    {
        var path = Path.Combine(root.FullName, Guid.NewGuid().ToString("N") + ".db");
        SqliteIndexWriter.Write(path, index);
        return path;
    }

    private static string Text<T>(T value) => JsonSerializer.Serialize(value, Json);

    [Theory]
    [InlineData("semantic")]
    [InlineData("documented")]
    public void CompleteRoundTripEqualsTheOriginalIndex(string fixture)
    {
        // Arrange
        var index = Fixture(fixture);
        var reader = new SqliteIndexReader(Write(index));

        // Act
        var full = reader.ReadFull(includeCatalog: true);
        var withoutCatalog = reader.ReadFull(includeCatalog: false);
        var header = reader.ReadHeader();

        // Assert
        Assert.Equal(Text(index), Text(full));
        Assert.Equal(Text(index with { Symbols = null, StructuralCatalog = null, Duplicates = null }), Text(withoutCatalog));
        Assert.Equal(Text(index with
        {
            Projects = [], Types = [], Methods = [], References = [], Symbols = null, StructuralCatalog = null,
            Duplicates = null, IncrementalState = null
        }), Text(header));
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("documented")]
    public void RowQueriesEqualTheInMemoryQueries(string fixture)
    {
        // Arrange
        var index = Fixture(fixture);
        var path = Write(index);
        var memory = new MemorySnapshotRows(index);
        var stored = new SqliteSnapshotRows(new SqliteIndexReader(path), new SqliteIndexReader(path).ReadHeader());
        var symbols = index.Symbols!;
        var unknown = "Library/Library.csproj::M:Missing.Method";

        // Act and assert: every method name, method ID, symbol ID/name/kind, owner and duplicate selection.
        foreach (var name in index.Methods.Select(method => method.Name).Distinct().Append("NoSuchMethod"))
            Assert.Equal(Text(memory.MethodsNamed(name)), Text(stored.MethodsNamed(name)));
        foreach (var id in index.Methods.Select(method => method.Id).Concat(symbols.Select(symbol => symbol.Id)).Append(unknown))
        {
            Assert.Equal(memory.HasMethod(id), stored.HasMethod(id));
            Assert.Equal(Text(memory.ReferencesOf(id)), Text(stored.ReferencesOf(id)));
            Assert.Equal(memory.HasSymbol(id), stored.HasSymbol(id));
            Assert.Equal(Text(memory.Symbol(id)), Text(stored.Symbol(id)));
            Assert.Equal(Text(memory.FactsOwnedBy(id)), Text(stored.FactsOwnedBy(id)));
            foreach (var scanLimit in new[] { 1, 7, Callers.MaximumFactsScanned })
                Assert.Equal(Text(memory.IncomingInvocations(id, scanLimit)), Text(stored.IncomingInvocations(id, scanLimit)));
            Assert.Equal(memory.DuplicateBodyTokenCount(id), stored.DuplicateBodyTokenCount(id));
            Assert.Equal(memory.DuplicateOmissionReasons(id), stored.DuplicateOmissionReasons(id));
            Assert.Equal(Text(memory.DuplicateCandidates(id, null)), Text(stored.DuplicateCandidates(id, null)));
        }
        foreach (var name in symbols.Select(symbol => symbol.Name).Distinct())
            foreach (var kind in new[] { "method", "type", "property", "field", "all" })
                Assert.Equal(Text(memory.SymbolsNamed(name, kind)), Text(stored.SymbolsNamed(name, kind)));
        foreach (var project in index.Projects.Select(project => project.Path).Append(null))
            Assert.Equal(Text(memory.DuplicateCandidates(null, project)), Text(stored.DuplicateCandidates(null, project)));
        Assert.Equal(memory.DuplicateOmittedMethodCount(), stored.DuplicateOmittedMethodCount());
        Assert.Equal(Text(memory.StructuralCoverage), Text(stored.StructuralCoverage));
        Assert.Equal(memory.SymbolIds(), stored.SymbolIds());
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("documented")]
    public void CallerTraversalAndDuplicateQueriesEqualTheCatalogScan(string fixture)
    {
        // Arrange
        var index = Fixture(fixture);
        var path = Write(index);
        var stored = new SqliteSnapshotRows(new SqliteIndexReader(path), new SqliteIndexReader(path).ReadHeader());

        foreach (var method in index.Methods)
        {
            foreach (var (depth, limit) in new[] { (1, 1), (1, 100), (3, 2), (5, 1000) })
            {
                // Act
                var expected = Callers.Traverse(index.StructuralCatalog!, method.Id, depth, limit);
                var actual = Callers.Traverse(stored, method.Id, depth, limit);

                // Assert
                Assert.Equal(Text(expected), Text(actual));
            }
            if (index.Duplicates is { } duplicates)
                Assert.Equal(Text(DuplicateSnapshotQueries.FindGroups(duplicates, index.IndexId, new QueryRequest(), method.Id)),
                    Text(DuplicateSnapshotQueries.FindGroups(stored, index.IndexId, new QueryRequest(), method.Id)));
        }
        if (index.Duplicates is { } catalog)
            Assert.Equal(Text(DuplicateSnapshotQueries.FindGroups(catalog, index.IndexId, new QueryRequest(Limit: 1))),
                Text(DuplicateSnapshotQueries.FindGroups(stored, index.IndexId, new QueryRequest(Limit: 1))));
    }

    [Fact]
    public void RepeatedStoredSearchRanksOnce()
    {
        // Arrange
        var index = Fixture("documented");
        var path = Write(index);
        var symbol = index.Symbols!.First(item => item.Kind == "method");
        var before = PurposeSearch.StoredRankingPasses;

        // Act
        Search();
        Search();

        // Assert
        Assert.Equal(before + 1, PurposeSearch.StoredRankingPasses);

        void Search()
        {
            using var corpus = new SqliteSearchCorpus(new SqliteIndexReader(path), index.StructuralCatalog!.Coverage);
            var result = PurposeSearch.Search(corpus, symbol.Name, "assisted", "all", new QueryRequest(),
                index.IndexId, "csharp", CancellationToken.None, null);
            Assert.NotEmpty(result.Page.Items);
        }
    }

    [Fact]
    public void StoredSearchPreloadsOwnerFacts()
    {
        // Arrange
        var index = Fixture("documented");
        var path = Write(index);
        var symbol = index.Symbols!.First(item => item.Kind == "method");
        using var corpus = new SqliteSearchCorpus(new SqliteIndexReader(path), index.StructuralCatalog!.Coverage);

        // Act
        var result = PurposeSearch.Search(corpus, symbol.Name, "assisted", "all", new QueryRequest(),
            index.IndexId, "csharp", CancellationToken.None, null);

        // Assert
        Assert.NotEmpty(result.Page.Items);
        Assert.Equal(0, corpus.FactReadsWhileSelecting);
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("documented")]
    public void PurposeSearchOverStoredPostingsEqualsTheCompleteScan(string fixture)
    {
        // Arrange
        var index = Fixture(fixture);
        var path = Write(index);
        var store = new SqliteIndexReader(path);
        var symbols = index.Symbols!;
        var queries = symbols.Select(symbol => symbol.Name)
            .Concat(symbols.SelectMany(symbol => symbol.Documentation.Sections.Select(section => section.Text)))
            .Concat(symbols.Select(symbol => symbol.Signature))
            .Concat(["add numbers", "author written description input", "test helper", "interface contract override", "zebra"])
            .Where(query => PurposeSearch.Tokenize(query).Length > 0)
            .Distinct().Take(80).ToArray();
        var described = symbols.Where(symbol => symbol.Kind is "method" or "type").Take(3).ToArray();
        var accepted = described.Select((symbol, i) => (symbol.Id, Explanation: Accepted(symbol.Id, index.IndexId,
            i == 0 ? "Computes zebra totals for the caller" : "Adds numbers and returns " + symbol.Name)))
            .ToDictionary(pair => pair.Id, pair => pair.Explanation, StringComparer.Ordinal);
        var project = index.Projects[0].Path;

        foreach (var query in queries)
            foreach (var mode in new[] { "names", "assisted" })
                foreach (var kind in new[] { "method", "type", "property", "field", "all" })
                    foreach (var request in new[] { new QueryRequest(Limit: 5), new QueryRequest(Project: project, Limit: 1000) })
                        foreach (var descriptions in new[] { null, accepted })
                        {
                            // Act
                            var expected = PurposeSearch.Search(symbols, index.StructuralCatalog!, query, mode, kind, request,
                                index.IndexId, acceptedExplanations: descriptions);
                            using var corpus = new SqliteSearchCorpus(store, index.StructuralCatalog!.Coverage);
                            var actual = PurposeSearch.Search(corpus, query, mode, kind, request, index.IndexId, "csharp",
                                CancellationToken.None, descriptions);

                            // Assert
                            Assert.Equal(Text(expected), Text(actual));
                        }
    }

    private static AcceptedExplanation Accepted(string symbolId, string indexId, string claim) =>
        new("ai-generated", Guid.NewGuid().ToString("N"), new string('a', 64), indexId, DateTimeOffset.UnixEpoch, "unverified",
            new ExplanationResponse(1, Guid.NewGuid().ToString("N"), indexId, symbolId, new string('a', 64),
                new ExplanationProvenance("test", "model", "v1", DateTimeOffset.UnixEpoch),
                [new ExplanationClaim(claim, "purpose", [])], []));

    [Fact]
    public async Task PublishedGenerationIsOneDatabaseOpenedHeaderOnly()
    {
        // Arrange
        var path = documented.IndexPath;

        // Act
        var snapshot = await IndexStore.OpenSnapshotAsync(path);
        var manifest = await GenerationStore.ResolveAsync(path);
        var full = await IndexStore.LoadSnapshotAsync(path);

        // Assert
        Assert.True(snapshot.IsHeaderOnly);
        Assert.True(snapshot.VerifiedGeneration);
        Assert.Empty(snapshot.Index.Methods);
        Assert.Equal(SqliteIndexSchema.ArtifactName, Path.GetFileName(snapshot.IndexPath));
        Assert.Equal(GenerationStore.StoreManifestVersion, manifest.Manifest.ManifestVersion);
        Assert.Equal(["index.db", "overview.md", "timings.json", "warnings.txt"], manifest.Manifest.Artifacts.Keys.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(snapshot.GenerationDirectory!, "index.json")));
        Assert.False(full.IsHeaderOnly);
        Assert.NotEmpty(full.Index.Methods);
        Assert.Equal(Text(full.Index.Methods.Where(method => method.Name == "Entry").ToArray()),
            Text(SnapshotQueries.FindMethods(snapshot, "Entry", new QueryRequest()).Items));
    }

    [Fact]
    public async Task PagesAreStableAndEqualAcrossStores()
    {
        // Arrange
        var snapshot = await IndexStore.OpenSnapshotAsync(documented.IndexPath);
        var full = await IndexStore.LoadSnapshotAsync(documented.IndexPath);
        var id = full.Index.References.MaxBy(pair => pair.Value.Length).Key;
        var total = full.Index.References[id].Length;
        var snapshotId = snapshot.Index.IndexId;

        // Act
        var all = SnapshotQueries.FindReferences(snapshot, id, new QueryRequest()).Items;
        var pages = Enumerable.Range(0, total + 1).Select(offset =>
            SnapshotQueries.FindReferences(snapshot, id, new QueryRequest(Limit: 1, Offset: offset, Snapshot: snapshotId))).ToArray();
        var memoryPages = Enumerable.Range(0, total + 1).Select(offset =>
            SnapshotQueries.FindReferences(full, id, new QueryRequest(Limit: 1, Offset: offset, Snapshot: snapshotId))).ToArray();

        // Assert
        Assert.True(total > 1);
        Assert.Equal(Text(all), Text(pages.SelectMany(page => page.Items).ToArray()));
        Assert.Equal(Text(memoryPages), Text(pages));
        Assert.All(pages.Take(total - 1), page => Assert.Equal(page.Pagination.Offset + 1, page.Pagination.NextOffset));
        Assert.False(pages[^1].Pagination.HasMore);
        Assert.Empty(pages[^1].Items);
        var mismatch = Record.Exception(() => SnapshotQueries.FindReferences(snapshot, id, new QueryRequest(Offset: 1, Snapshot: "other")));
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(mismatch).Code);
    }

    private string CopyGeneration(LoadedSnapshot snapshot)
    {
        var copy = Directory.CreateDirectory(Path.Combine(root.FullName, "copy-" + Guid.NewGuid().ToString("N")));
        foreach (var file in Directory.GetFiles(snapshot.GenerationDirectory!))
            File.Copy(file, Path.Combine(copy.FullName, Path.GetFileName(file)));
        return Path.Combine(copy.FullName, SqliteIndexSchema.ArtifactName);
    }

    [Fact]
    public async Task TruncatedDamagedOrIncompleteDatabasesFailExplicitly()
    {
        // Arrange
        var snapshot = await IndexStore.OpenSnapshotAsync(documented.IndexPath);
        var truncated = CopyGeneration(snapshot);
        await using (var file = new FileStream(truncated, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 4096);
        var header = CopyGeneration(snapshot);
        await using (var file = new FileStream(header, FileMode.Open, FileAccess.Write)) file.Write(new byte[16]);
        var pages = CopyGeneration(snapshot);
        await using (var file = new FileStream(pages, FileMode.Open, FileAccess.Write))
        {
            file.Position = 4096 * 2;
            file.Write(new byte[file.Length - file.Position]);
        }
        var flipped = CopyGeneration(snapshot);
        await using (var file = new FileStream(flipped, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = file.Length / 2;
            var value = file.ReadByte();
            file.Position = file.Length / 2;
            file.WriteByte((byte)(value ^ 0x5A));
        }
        var incomplete = Path.Combine(root.FullName, "incomplete.db");
        using (var writer = SqliteIndexWriter.Create(incomplete)) writer.WriteHeader(documented.Index);

        // Act
        var truncatedFailure = await Record.ExceptionAsync(() => IndexStore.OpenSnapshotAsync(truncated));
        var headerFailure = await Record.ExceptionAsync(() => IndexStore.OpenSnapshotAsync(header));
        var pagesSnapshot = await Record.ExceptionAsync(async () =>
        {
            var opened = await IndexStore.OpenSnapshotAsync(pages);
            SnapshotQueries.FindMethods(opened, "Entry", new QueryRequest());
        });
        var flippedOpened = await IndexStore.OpenSnapshotAsync(flipped);
        var flippedVerification = await Record.ExceptionAsync(() => IndexStore.VerifyStoreAsync(flippedOpened));
        var incompleteFailure = Record.Exception(() => new SqliteIndexReader(incomplete).ReadHeader());

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(truncatedFailure).Code);
        Assert.Contains("truncated", truncatedFailure.Message);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(headerFailure).Code);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(pagesSnapshot).Code);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(flippedVerification).Code);
        Assert.Contains("checksum", flippedVerification.Message);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(incompleteFailure).Code);
        Assert.Contains("incomplete", incompleteFailure.Message);
        await IndexStore.VerifyStoreAsync(snapshot);
    }

    [Fact]
    public async Task FailedOrCancelledPublicationKeepsThePreviousGenerationActive()
    {
        // Arrange
        var previous = await IndexStore.OpenSnapshotAsync(documented.IndexPath);
        var next = SnapshotTestIdentity.NewGeneration(documented.Index);
        GenerationArtifact[] Artifacts() =>
        [
            new(SqliteIndexSchema.ArtifactName, null, (path, token) => { SqliteIndexWriter.Write(path, next, token); return Task.CompletedTask; }),
            new("overview.md", (stream, token) => stream.WriteAsync("o"u8.ToArray(), token).AsTask()),
            new("warnings.txt", (stream, token) => stream.WriteAsync("w"u8.ToArray(), token).AsTask()),
            new("timings.json", (stream, token) => stream.WriteAsync("{}"u8.ToArray(), token).AsTask())
        ];
        using var cancelled = new CancellationTokenSource();
        var cancelling = new GenerationStore((point, _) =>
        {
            if (point == PublishCheckpoint.BeforeManifestCommit) cancelled.Cancel();
            return Task.CompletedTask;
        });
        var failing = new GenerationStore((point, _) => point == PublishCheckpoint.AfterArtifact
            ? throw new IOException("injected disk failure") : Task.CompletedTask);

        // Act
        var cancellation = await Record.ExceptionAsync(() => cancelling.PublishAsync(next.Solution, next.IndexId, Artifacts(),
            _ => Task.CompletedTask, cancelled.Token));
        next = SnapshotTestIdentity.NewGeneration(documented.Index);
        var failure = await Record.ExceptionAsync(() => failing.PublishAsync(next.Solution, next.IndexId, Artifacts(),
            _ => Task.CompletedTask));
        var active = await IndexStore.OpenSnapshotAsync(documented.IndexPath);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellation);
        Assert.IsType<IOException>(failure);
        Assert.Equal(previous.Index.IndexId, active.Index.IndexId);
        Assert.Equal(Text(SnapshotQueries.FindMethods(previous, "Entry", new QueryRequest())),
            Text(SnapshotQueries.FindMethods(active, "Entry", new QueryRequest())));
    }

    [Fact]
    public async Task ReadersKeepTheirGenerationWhilePublicationsCommit()
    {
        // Arrange
        var first = await IndexStore.OpenSnapshotAsync(documented.IndexPath);
        var expected = Text(SnapshotQueries.FindMethods(first, "Entry", new QueryRequest()).Items);
        var expectedDocumentation = Text(SnapshotQueries.GetDocumentation(first, documented.Index.Symbols![0].Id));
        using var stop = new CancellationTokenSource();
        var observed = new List<string>();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            try
            {
                Assert.Equal(expected, Text(SnapshotQueries.FindMethods(first, "Entry", new QueryRequest()).Items));
                var current = await IndexStore.OpenSnapshotAsync(documented.IndexPath);
                Assert.Equal(expected, Text(SnapshotQueries.FindMethods(current, "Entry", new QueryRequest()).Items));
                Assert.Equal(expectedDocumentation, Text(SnapshotQueries.GetDocumentation(current, documented.Index.Symbols![0].Id)));
                lock (observed) observed.Add(current.Index.IndexId);
            }
            catch (IndexFormatException exception)
            {
                throw new Xunit.Sdk.XunitException(exception.Code + ": " + exception.Message + " " + string.Join(" | ", exception.Diagnostics));
            }
        });

        // Act
        var published = new List<string>();
        for (var i = 0; i < 8; i++)
            published.Add((await IndexStore.PublishAsync(SnapshotTestIdentity.NewGeneration(documented.Index))).Index.IndexId);
        await Task.Delay(200);
        stop.Cancel();
        await reader;

        // Assert
        Assert.NotEmpty(observed);
        Assert.All(observed, id => Assert.Contains(id, published.Prepend(first.Index.IndexId)));
        Assert.Equal(expected, Text(SnapshotQueries.FindMethods(first, "Entry", new QueryRequest()).Items));
        Assert.Equal(published[^1], (await IndexStore.OpenSnapshotAsync(documented.IndexPath)).Index.IndexId);
    }

    [Fact]
    public async Task JsonExportReproducesTheLegacyStoreBytesAndStaysQueryable()
    {
        // Arrange
        var snapshot = await IndexStore.LoadSnapshotAsync(documented.IndexPath);
        var index = snapshot.Index;
        var output = Path.Combine(root.FullName, "export");
        var legacyIndex = JsonSerializer.SerializeToUtf8Bytes(index with
            { Symbols = null, DocumentationCoverage = null, StructuralCatalog = null, Duplicates = null }, Json);
        var legacyCatalog = JsonSerializer.SerializeToUtf8Bytes(new SymbolCatalogFile(index.Symbols, index.DocumentationCoverage,
            index.StructuralCatalog, index.Duplicates) { IndexId = index.IndexId, Solution = index.Solution }, Json);

        // Act
        var export = await IndexStore.ExportJsonAsync(documented.IndexPath, output);
        var legacy = await IndexStore.OpenSnapshotAsync(export.IndexJson);
        var reloaded = await IndexStore.LoadSnapshotAsync(export.IndexJson);
        var again = await Record.ExceptionAsync(() => IndexStore.ExportJsonAsync(documented.IndexPath, output));

        // Assert
        Assert.Equal(legacyIndex, await File.ReadAllBytesAsync(export.IndexJson));
        Assert.Equal(legacyCatalog, await File.ReadAllBytesAsync(export.CatalogJson));
        Assert.False(legacy.IsHeaderOnly);
        Assert.True(legacy.VerifiedGeneration);
        Assert.Equal(Text(index), Text(reloaded.Index));
        Assert.Equal(Text(SnapshotQueries.FindMethods(snapshot, "Entry", new QueryRequest())),
            Text(SnapshotQueries.FindMethods(legacy, "Entry", new QueryRequest())));
        Assert.IsType<ArgumentException>(again);
    }

    [Fact]
    public async Task CliResponsesFromTheStoreEqualTheLegacyJsonGeneration()
    {
        // Arrange
        var full = await IndexStore.LoadSnapshotAsync(documented.IndexPath);
        var output = Path.Combine(root.FullName, "cli-export");
        var exported = await Cli("export-json", "--index", documented.IndexPath, "--output", output);
        Assert.True(exported.Exit == 0, exported.Error);
        var legacy = Path.Combine(output, "index.json");
        var method = full.Index.References.MaxBy(pair => pair.Value.Length).Key;
        const string describe = "Library/Library.csproj::M:Docs.Methods.Describe(System.Int32)";
        string[][] queries =
        [
            ["find-symbol", "--name", "Entry"],
            ["find-symbol", "--name", "Entry", "--kind", "all", "--limit", "2"],
            ["find-symbol", "--name", "Calculate", "--project", "Library/Library.csproj"],
            ["find-references", "--symbol", method],
            ["find-references", "--symbol", "Library/Library.csproj::T:Docs.Entry"],
            ["get-documentation", "--symbol", describe],
            ["get-documentation", "--symbol", "Library/Library.csproj::M:Missing"],
            ["search-symbols", "--query", "author written description"],
            ["search-symbols", "--query", "entry", "--mode", "names", "--kind", "all"],
            ["get-facts", "--symbol", describe],
            ["find-callers", "--symbol", "Library/Library.csproj::M:Docs.Methods.Entry(System.Int32)", "--depth", "3"],
            ["find-duplicates"],
            ["coverage"],
            ["index-status"]
        ];

        foreach (var query in queries)
        {
            // Act
            var store = await Cli([.. query, "--index", documented.IndexPath]);
            var json = await Cli([.. query, "--index", legacy]);

            // Assert
            Assert.Equal(json.Exit, store.Exit);
            Assert.Equal(Normalize(json.Exit == 0 ? json.Output : json.Error), Normalize(store.Exit == 0 ? store.Output : store.Error));
        }
    }

    /// <summary>Documented volatile fields: the check time and paths into the two generation directories.</summary>
    private static string Normalize(string response)
    {
        var node = JsonNode.Parse(response)!.AsObject();
        foreach (var field in new[] { "warningsFile", "warningMessage", "coverageMessage" }) node.Remove(field);
        if (node["freshness"] is JsonObject freshness) freshness.Remove("checkedAtUtc");
        return node.ToJsonString();
    }

    [Fact]
    public async Task UnavailableSqliteReturnsAnExplicitJsonError()
    {
        // Act
        var result = await Cli(new Dictionary<string, string> { [SqliteAvailability.SimulateVariable] = "test" },
            "find-symbol", "--index", documented.IndexPath, "--name", "Entry");
        var indexing = await Cli(new Dictionary<string, string> { [SqliteAvailability.SimulateVariable] = "test" },
            "index", "--solution", documented.Index.Solution);

        // Assert
        foreach (var failure in new[] { result, indexing })
        {
            Assert.Equal(1, failure.Exit);
            Assert.Equal("", failure.Output);
            var error = JsonNode.Parse(failure.Error)!["error"]!;
            Assert.Equal("sqlite_unavailable", error["code"]!.GetValue<string>());
            Assert.Contains("Simulated: test", error["message"]!.GetValue<string>());
            Assert.NotEmpty(error["diagnostics"]!.AsArray());
        }
    }

    private sealed record CliResult(int Exit, string Output, string Error);

    private static Task<CliResult> Cli(params string[] arguments) => Cli(new Dictionary<string, string>(), arguments);

    private static async Task<CliResult> Cli(Dictionary<string, string> environment, params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository!.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }
}

[CollectionDefinition(nameof(SqliteProbeCollection), DisableParallelization = true)]
public sealed class SqliteProbeCollection;

/// <summary>Replaces the process-wide availability probe, so it never runs beside other tests.</summary>
[Collection(nameof(SqliteProbeCollection))]
public sealed class SqliteAvailabilityTests
{
    [Fact]
    public void BundledSqliteIsAvailableWithFullTextSearch()
    {
        // Act
        var status = SqliteAvailability.Check();

        // Assert
        Assert.True(status.Available, status.Failure);
        Assert.True(status.Fts5);
        Assert.True(Version.Parse(status.Version!) >= Version.Parse(SqliteAvailability.MinimumVersion));
    }

    [Fact]
    public void InjectedFailureBlocksStoreAccessWithInstructions()
    {
        // Arrange
        SqliteAvailability.Probe = () => new SqliteStatus(false, null, false, "Injected failure.")
            { Instructions = SqliteAvailability.InstructionsFor(new SqliteStatus(false, null, false, "Injected failure.")) };
        try
        {
            // Act
            var failure = Record.Exception(SqliteAvailability.Require);
            var writing = Record.Exception(() => SqliteIndexWriter.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db")));

            // Assert
            var unavailable = Assert.IsType<SqliteUnavailableException>(failure);
            Assert.Equal("sqlite_unavailable", unavailable.Code);
            Assert.Contains("Injected failure.", unavailable.Message);
            Assert.Contains(unavailable.Diagnostics, line => line.Contains("dotnet tool", StringComparison.Ordinal));
            Assert.IsType<SqliteUnavailableException>(writing);
        }
        finally
        {
            SqliteAvailability.Probe = null;
        }
        Assert.True(SqliteAvailability.Check().Available);
    }
}
