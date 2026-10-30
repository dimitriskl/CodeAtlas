using System.Diagnostics;
using System.Text.Json;
using CodeAtlas.Persistence;
using CodeAtlas.Storage;
using Xunit;

namespace CodeAtlas.Tests;

/// <summary>One file's method and reference rows on a private working copy, compared with a full rebuild.</summary>
public sealed partial class SqliteFileUpdateTests
{
    private const string CalculatorFile = "Library/Calculator.cs";
    private const string Calculate = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)";

    [Fact]
    public async Task ReplacingOneFilesRowsEqualsAFullRebuild()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var original = Path.Combine(fixture.Root, "original.db");
        await IndexAsync(fixture.Solution, original);
        var workingCopy = Path.Combine(fixture.Root, "working.db");
        File.Copy(original, workingCopy);
        var source = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, source.Replace(
            "public static int LocalCaller() => Calculate(1);",
            "public static int LocalCaller() => Calculate(1);\n    public static int Extra() => Calculate(4);",
            StringComparison.Ordinal));
        var expected = await IndexAsync(fixture.Solution, Path.Combine(fixture.Root, "expected.db"));
        var methods = expected.Rows.Methods.Where(method => method.Location.File == CalculatorFile).ToArray();
        var references = FileReferences(expected.Rows.References, methods);
        var before = ReadRows(workingCopy);
        var beforeReferences = FindReferences(workingCopy, Calculate);

        // Act
        SqliteIndexWriter.ReplaceFileRows(workingCopy, CalculatorFile, methods, references);

        // Assert
        var actual = ReadRows(workingCopy);
        var actualReferences = FindReferences(workingCopy, Calculate);
        Assert.Equal(beforeReferences.Length + 1, actualReferences.Length);
        Assert.Equal(FindReferences(expected.Database, Calculate), actualReferences);
        Assert.Equal(expected.Rows.Methods, actual.Methods);
        EqualReferences(expected.Rows.References, actual.References);
        Assert.Equal(before.Methods.Where(method => method.Location.File != CalculatorFile).ToArray(),
            actual.Methods.Where(method => method.Location.File != CalculatorFile).ToArray());
        EqualReferences(Outside(before.References), Outside(actual.References));
    }

    [Fact]
    public async Task InMemoryEditThenReplacingFileRowsEqualsAFullRebuild()
    {
        // Arrange
        using var sourceFixture = await SampleCopy.CreateAsync();
        var original = Path.Combine(sourceFixture.Root, "original.db");
        await IndexAsync(sourceFixture.Solution, original);
        var workingCopy = Path.Combine(sourceFixture.Root, "working.db");
        File.Copy(original, workingCopy);
        var source = await File.ReadAllTextAsync(sourceFixture.Calculator);
        var edited = source.Replace(
            "public static int LocalCaller() => Calculate(1);",
            "public static int LocalCaller() => Calculate(1);\n    public static int Extra() => Calculate(4);",
            StringComparison.Ordinal);
        using var analysis = await SolutionAnalysis.LoadAsync(sourceFixture.Solution);
        analysis.ApplyDocumentText(sourceFixture.Calculator, edited);
        var beforeReferences = FindReferences(workingCopy, Calculate);
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        var rows = await analysis.FileRowsAsync(sourceFixture.Calculator);

        // Assert
        SqliteIndexWriter.ReplaceFileRows(workingCopy, CalculatorFile, rows.Methods, rows.References);
        var actual = ReadRows(workingCopy);
        var actualReferences = FindReferences(workingCopy, Calculate);
        Assert.Equal(beforeReferences.Length + 1, actualReferences.Length);
        Assert.Equal(FindReferences(expected.Database, Calculate), actualReferences);
        Assert.Equal(expected.Rows.Methods, actual.Methods);
        EqualReferences(expected.Rows.References, actual.References);
    }

    [Fact]
    public async Task UpdatingOneFileInTheWorkingCopyEqualsAFullRebuild()
    {
        // Arrange
        using var sourceFixture = await SampleCopy.CreateAsync();
        var original = Path.Combine(sourceFixture.Root, "original.db");
        await IndexAsync(sourceFixture.Solution, original);
        var workingCopy = Path.Combine(sourceFixture.Root, "working.db");
        File.Copy(original, workingCopy);
        var edited = EditedCalculator(await File.ReadAllTextAsync(sourceFixture.Calculator));
        using var analysis = await SolutionAnalysis.LoadAsync(sourceFixture.Solution);
        var beforeReferences = FindReferences(workingCopy, Calculate);
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        await analysis.UpdateWorkingCopyAsync(workingCopy, sourceFixture.Calculator, edited);

        // Assert
        var actual = ReadRows(workingCopy);
        var actualReferences = FindReferences(workingCopy, Calculate);
        Assert.Equal(beforeReferences.Length + 1, actualReferences.Length);
        Assert.Equal(FindReferences(expected.Database, Calculate), actualReferences);
        Assert.Equal(expected.Rows.Methods, actual.Methods);
        EqualReferences(expected.Rows.References, actual.References);
    }

    [Fact]
    public async Task UpdatingAPathThatIsNotOpenLeavesTheWorkingCopyUnchanged()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var workingCopy = Path.Combine(fixture.Root, "working.db");
        await IndexAsync(fixture.Solution, workingCopy);
        var before = ReadRows(workingCopy);
        using var analysis = await SolutionAnalysis.LoadAsync(fixture.Solution);
        var missing = Path.Combine(fixture.Root, "Library", "missing.cs");

        // Act
        var failure = await Record.ExceptionAsync(() => analysis.UpdateWorkingCopyAsync(workingCopy, missing, "class Missing {}"));

        // Assert
        Assert.Equal("The path is not an open source document: " + missing, Assert.IsType<AnalysisException>(failure).Message);
        var actual = ReadRows(workingCopy);
        Assert.Equal(before.Methods, actual.Methods);
        EqualReferences(before.References, actual.References);
    }

    [Fact]
    public async Task PublishingTheWorkingCopyEqualsAFullRebuild()
    {
        // Arrange
        using var sourceFixture = await SampleCopy.CreateAsync();
        var published = await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(sourceFixture.Solution));
        var previousDatabase = published.IndexPath;
        var previousBytes = await File.ReadAllBytesAsync(previousDatabase);
        var beforeReferences = IndexStore.FindReferences(published.Index, Calculate);
        var workingCopy = Path.Combine(sourceFixture.Root, "working.db");
        File.Copy(previousDatabase, workingCopy);
        var edited = EditedCalculator(await File.ReadAllTextAsync(sourceFixture.Calculator));
        using (var analysis = await SolutionAnalysis.LoadAsync(sourceFixture.Solution))
            await analysis.UpdateWorkingCopyAsync(workingCopy, sourceFixture.Calculator, edited);
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        await IndexStore.PublishWorkingCopyAsync(sourceFixture.Solution, workingCopy);

        // Assert
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(sourceFixture.Solution));
        var freshness = await IndexStore.CheckFreshnessAsync(opened.Index);
        var actualReferences = SnapshotQueries.FindReferences(opened, Calculate, new QueryRequest()).Items;
        var actual = ReadRows(opened.IndexPath);
        Assert.False(freshness.IsStale);
        Assert.Equal(beforeReferences.Count + 1, actualReferences.Length);
        Assert.Equal(FindReferences(expected.Database, Calculate), actualReferences);
        Assert.Equal(expected.Rows.Methods, actual.Methods);
        EqualReferences(expected.Rows.References, actual.References);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previousDatabase));
    }

    [Fact]
    public async Task PublishingWhenDiskDiffersFromTheAppliedTextIsStale()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var published = await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        var previousBytes = await File.ReadAllBytesAsync(published.IndexPath);
        var workingCopy = Path.Combine(fixture.Root, "working.db");
        File.Copy(published.IndexPath, workingCopy);
        var original = await File.ReadAllTextAsync(fixture.Calculator);
        var edited = EditedCalculator(original);
        using (var analysis = await SolutionAnalysis.LoadAsync(fixture.Solution))
            await analysis.UpdateWorkingCopyAsync(workingCopy, fixture.Calculator, edited);
        await File.WriteAllTextAsync(fixture.Calculator, original);

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.PublishWorkingCopyAsync(fixture.Solution, workingCopy));

        // Assert
        Assert.Contains(CalculatorFile, Assert.IsType<StaleIndexException>(failure).Diagnostics);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(published.IndexPath));
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        Assert.Equal(published.Index.IndexId, opened.Index.IndexId);
        Assert.Equal(IndexStore.FindReferences(published.Index, Calculate), SnapshotQueries.FindReferences(opened, Calculate, new QueryRequest()).Items);
    }

    [Fact]
    public async Task LiveSessionEditIsVisibleWithoutAStaleRefusal()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
        var before = await session.FindReferencesAsync(Calculate);
        var edited = EditedCalculator(await File.ReadAllTextAsync(fixture.Calculator));

        // Act
        await session.ApplyAsync(fixture.Calculator, edited);

        // Assert
        var after = await session.FindReferencesAsync(Calculate);
        Assert.Equal(before.Count + 1, after.Count);
    }

    [Fact]
    public async Task LiveSessionEditPublishesTheEditedFilesCatalogRows()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
        var edited = EditedCalculator(await File.ReadAllTextAsync(fixture.Calculator));
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        await session.ApplyAsync(fixture.Calculator, edited);

        // Assert
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        var found = SnapshotQueries.FindSymbols(opened, "Extra", "method", new QueryRequest());
        Assert.Equal(["Extra"], found.Items.Select(symbol => symbol.Name).ToArray());
        var search = await PurposeSnapshotQueries.SearchAsync(opened, "Extra", "names", "method", new QueryRequest());
        Assert.Contains(search.Page.Items, item => item.Symbol.Name == "Extra");
        Assert.Equal(FileCatalog(expected.Database), FileCatalog(opened.IndexPath));
        var stored = new SqliteIndexReader(opened.IndexPath).Use(session => session.Duplicates(CancellationToken.None));
        Assert.Equal(opened.Index.IndexId, stored!.Scan!.IndexId);
    }

    [Fact]
    public async Task LiveSessionEditPublishesTypesForTheFile()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
        var edited = AddedType(await File.ReadAllTextAsync(fixture.Calculator));
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        await session.ApplyAsync(fixture.Calculator, edited);

        // Assert
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        var found = SnapshotQueries.FindSymbols(opened, "Added", "type", new QueryRequest());
        Assert.Equal(["Added"], found.Items.Select(symbol => symbol.Name).ToArray());
        Assert.Equal(FileTypes(expected.Database), FileTypes(opened.IndexPath));
    }

    [Fact]
    public async Task SecondEditPublishesRowsMatchingAFullRebuild()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
        var original = await File.ReadAllTextAsync(fixture.Calculator);
        var once = EditedCalculator(original);
        var twice = EditedAgain(once);
        await session.ApplyAsync(fixture.Calculator, once);
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, twice);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));

        // Act
        await session.ApplyAsync(fixture.Calculator, twice);

        // Assert
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        Assert.Equal(TreeHash(expected.Database), TreeHash(opened.IndexPath));
        Assert.Equal(ReadRows(expected.Database).Methods, ReadRows(opened.IndexPath).Methods);
        Assert.Equal(FileCatalog(expected.Database), FileCatalog(opened.IndexPath));
    }

    [Fact]
    public async Task LiveSessionEditRefreshesOverviewCountsAndWarnings()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
        var edited = AddedType(await File.ReadAllTextAsync(fixture.Calculator));
        using var editedFixture = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(editedFixture.Calculator, edited);
        var expected = await IndexAsync(editedFixture.Solution, Path.Combine(editedFixture.Root, "expected.db"));
        var expectedRows = new SqliteIndexReader(expected.Database).Use(reader => (Types: reader.Types().Length, Methods: reader.Methods(null).Length));

        // Act
        await session.ApplyAsync(fixture.Calculator, edited);

        // Assert
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        var overview = await File.ReadAllTextAsync(Path.Combine(opened.GenerationDirectory!, "overview.md"));
        var warnings = await File.ReadAllTextAsync(opened.WarningsPath!);
        Assert.Contains("`Sample.Added`", overview);
        Assert.Contains($"source types: {expectedRows.Types}; methods: {expectedRows.Methods}.", overview);
        Assert.Contains(opened.Index.IndexId, warnings);
    }

    [Fact]
    public async Task FailedPublicationLeavesTheCurrentGenerationUnchanged()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var published = await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        var previousDatabase = published.IndexPath;
        var previousBytes = await File.ReadAllBytesAsync(previousDatabase);
        var workingCopy = Path.Combine(fixture.Root, "working.db");
        File.Copy(previousDatabase, workingCopy);

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.PublishWorkingCopyAsync(fixture.Solution, workingCopy,
            (point, _) => point == PublishCheckpoint.BeforeManifestCommit
                ? Task.FromException(new IOException("injected disk failure"))
                : Task.CompletedTask));

        // Assert
        Assert.Equal("snapshot_io_failed", Assert.IsType<IndexFormatException>(failure).Code);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previousDatabase));
        var opened = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(fixture.Solution));
        Assert.Equal(published.Index.IndexId, opened.Index.IndexId);
        Assert.Equal(IndexStore.FindReferences(published.Index, Calculate), SnapshotQueries.FindReferences(opened, Calculate, new QueryRequest()).Items);
    }

    [Fact]
    public async Task InjectedFailureLeavesTheWorkingCopyUnchanged()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var workingCopy = Path.Combine(fixture.Root, "working.db");
        await IndexAsync(fixture.Solution, workingCopy);
        var before = ReadRows(workingCopy);
        var methods = before.Methods.Where(method => method.Location.File == CalculatorFile && method.Name != "LocalCaller").ToArray();
        var references = FileReferences(before.References, before.Methods.Where(method => method.Location.File == CalculatorFile).ToArray());
        SqliteIndexWriter.BeforeFileRowCommit = () => throw new IOException("injected disk failure");
        try
        {
            // Act
            var failure = Record.Exception(() => SqliteIndexWriter.ReplaceFileRows(workingCopy, CalculatorFile, methods, references));

            // Assert
            Assert.Equal("injected disk failure", Assert.IsType<IOException>(failure).Message);
            var actual = ReadRows(workingCopy);
            Assert.Equal(before.Methods, actual.Methods);
            EqualReferences(before.References, actual.References);
        }
        finally
        {
            SqliteIndexWriter.BeforeFileRowCommit = null;
        }
    }

    private static string FileCatalog(string database)
    {
        var reader = new SqliteIndexReader(database);
        return reader.Use(session =>
        {
            var symbols = session.Symbols(CancellationToken.None).Where(symbol => symbol.Location.File == CalculatorFile).ToArray();
            var ids = symbols.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);
            var structural = session.StructuralCatalog(CancellationToken.None);
            var duplicates = session.Duplicates(CancellationToken.None);
            var facts = structural!.Facts.Where(fact => fact.Evidence.Location.Start.File == CalculatorFile).ToArray();
            var analysis = duplicates!.Analysis!;
            var scan = duplicates.Scan!;
            return JsonSerializer.Serialize(new
            {
                symbols,
                facts,
                search = session.SearchPostingsForFile(CalculatorFile),
                bodies = analysis.Bodies.Where(body => body.Location.Start.File == CalculatorFile).ToArray(),
                omissions = analysis.Omissions.Where(omission => omission.Location.Start.File == CalculatorFile).ToArray(),
                scanOmissions = scan.Omissions.Where(omission => omission.Location.Start.File == CalculatorFile).ToArray(),
                candidates = scan.Candidates.Where(candidate => candidate.Left.Location.Start.File == CalculatorFile
                    || candidate.Right.Location.Start.File == CalculatorFile).ToArray(),
                groups = duplicates.Groups.Where(group => group.MemberIds.Any(ids.Contains)).ToArray()
            }, SqliteIndexSchema.Json);
        });
    }

    private static IndexedType[] FileTypes(string database) =>
        new SqliteIndexReader(database).Use(session => session.Types().Where(type => type.Location.File == CalculatorFile).ToArray());

    private static (string Content, string? Surface) TreeHash(string database)
    {
        var state = new SqliteIndexReader(database).Use(session => session.IncrementalState());
        var tree = state!.Projects.SelectMany(project => project.Trees).Single(tree => tree.OriginPath == CalculatorFile);
        return (tree.ContentHash, tree.SurfaceHash);
    }

    private static string AddedType(string source) => source + "\npublic sealed class Added {}\n";

    private static string EditedAgain(string source) => source.Replace(
        "public static int Extra() => Calculate(4);",
        "public static int Extra() => Calculate(5);\n    public static int Again() => Calculate(6);",
        StringComparison.Ordinal);

    private static string EditedCalculator(string source) => source.Replace(
        "public static int LocalCaller() => Calculate(1);",
        "public static int LocalCaller() => Calculate(1);\n    public static int Extra() => Calculate(4);",
        StringComparison.Ordinal);

    private static Dictionary<string, SourceReference[]> FileReferences(
        Dictionary<string, SourceReference[]> all, MethodInfo[] methods)
    {
        var references = new Dictionary<string, SourceReference[]>(StringComparer.Ordinal);
        foreach (var (id, rows) in all)
        {
            var mine = rows.Where(row => row.Location.File == CalculatorFile).ToArray();
            if (mine.Length > 0 || Array.Exists(methods, method => method.Id == id))
                references[id] = mine;
        }
        return references;
    }

    private static Dictionary<string, SourceReference[]> Outside(Dictionary<string, SourceReference[]> references)
    {
        var outside = new Dictionary<string, SourceReference[]>(StringComparer.Ordinal);
        foreach (var (id, rows) in references)
        {
            var mine = rows.Where(row => row.Location.File != CalculatorFile).ToArray();
            if (mine.Length > 0)
                outside[id] = mine;
        }
        return outside;
    }

    private static void EqualReferences(Dictionary<string, SourceReference[]> expected, Dictionary<string, SourceReference[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (id, rows) in expected)
            Assert.Equal(rows, actual[id]);
    }

    private static async Task<IndexedDatabase> IndexAsync(string solution, string database)
    {
        SqliteIndexWriter.Write(database, await SolutionAnalysis.CreateIndexAsync(solution));
        return new IndexedDatabase(database, ReadRows(database));
    }

    private static (MethodInfo[] Methods, Dictionary<string, SourceReference[]> References) ReadRows(string database)
    {
        var reader = new SqliteIndexReader(database);
        return reader.Use(session => (session.Methods(null), session.AllReferences(CancellationToken.None)));
    }

    private static SourceReference[] FindReferences(string database, string methodId)
    {
        var reader = new SqliteIndexReader(database);
        var snapshot = new LoadedSnapshot(reader.ReadHeader(), database, null, false)
        {
            Store = reader,
            ArtifactName = SqliteIndexSchema.ArtifactName
        };
        return SnapshotQueries.FindReferences(snapshot, methodId, new QueryRequest()).Items;
    }

    private sealed record IndexedDatabase(string Database,
        (MethodInfo[] Methods, Dictionary<string, SourceReference[]> References) Rows);

    private sealed class SampleCopy : IDisposable
    {
        internal string Root { get; }
        internal string Solution => Path.Combine(Root, "Sample.slnx");
        internal string Calculator => Path.Combine(Root, "Library", "Calculator.cs");

        private SampleCopy(string root) => Root = root;

        internal static async Task<SampleCopy> CreateAsync()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new InvalidOperationException("Required checkout not found.");
            var root = Path.Combine(Path.GetTempPath(), "codeatlas-file-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var relative in new[] { "Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs",
                         "Library/packages.lock.json", "Caller/Caller.csproj", "Caller/Consumer.cs", "Caller/packages.lock.json" })
            {
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repository.FullName, "tests", "Fixtures", relative), target);
            }
            foreach (var name in new[] { "Directory.Build.props", "Version.props" })
                File.Copy(Path.Combine(repository.FullName, name), Path.Combine(root, name));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "restore", "Sample.slnx", "--locked-mode" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await output + await error);
            return new SampleCopy(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
