using Xunit;

namespace CodeAtlas.Tests;

public sealed class DocumentationRefreshTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private sealed class ImmediateProgress(Action<IndexProgress> action) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => action(value);
    }

    [Theory]
    [InlineData("wording")]
    [InlineData("cref")]
    public async Task DocumentationAndCrefEditsRefreshByFullFallbackAndEqualCleanAnalysis(string edit)
    {
        // Arrange
        var isolated = await PhysicalRefreshFixture.CreateAsync();
        var source = await File.ReadAllTextAsync(isolated.Calculator);
        source = source.Replace("    public static int Calculate(int value)",
            "    /// <summary>Original integer description.</summary>\n    public static int Calculate(int value)", StringComparison.Ordinal)
            .Replace("    public static int LocalCaller()", "    /// <summary>See <see cref=\"Calculate(int)\"/>.</summary>\n    public static int LocalCaller()", StringComparison.Ordinal);
        await File.WriteAllTextAsync(isolated.Calculator, source);
        var baseline = await SolutionAnalysis.CreateIndexAsync(isolated.Solution);
        await IndexStore.PublishAsync(baseline);
        var replacement = edit == "wording"
            ? source.Replace("Original integer description.", "Modified integer description.", StringComparison.Ordinal)
            : source.Replace("cref=\"Calculate(int)\"", "cref=\"Calculate(string)\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(isolated.Calculator, replacement);
        var expected = await SolutionAnalysis.CreateIndexAsync(isolated.Solution);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(isolated.Solution);

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal("full", actual.Refresh!.Mode);
        Assert.Equal("declaration_surface_changed", actual.Refresh.FallbackReason);
        Assert.Equal(actual.Methods.Length, actual.Refresh.ReferenceSearches);
        if (edit == "wording")
            Assert.Equal("Modified integer description.", actual.Symbols!.Single(symbol => symbol.Id.EndsWith("Calculate(System.Int32)", StringComparison.Ordinal)).Documentation.Sections.Single().Text);
        else
            Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(baseline.References), System.Text.Json.JsonSerializer.Serialize(actual.References));
    }

    [Fact]
    public async Task BodyOnlyLineShiftRecomputesCommentPositionsAndEqualsCleanAnalysis()
    {
        // Arrange
        var isolated = await PhysicalRefreshFixture.CreateAsync();
        var source = await File.ReadAllTextAsync(isolated.Calculator);
        source = source.Replace("    public static int Calculate(int value)",
            "    /// <summary>First overload.</summary>\n    public static int Calculate(int value)", StringComparison.Ordinal)
            .Replace("    public static string Calculate(string value)",
                "    /// <summary>Second overload.</summary>\n    public static string Calculate(string value)", StringComparison.Ordinal);
        await File.WriteAllTextAsync(isolated.Calculator, source);
        var baseline = await SolutionAnalysis.CreateIndexAsync(isolated.Solution);
        await IndexStore.PublishAsync(baseline);
        await File.WriteAllTextAsync(isolated.Calculator, source.Replace("=> value + 1;", "=>\n        value + 2;", StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(isolated.Solution);

        // Act
        var actual = await SolutionAnalysis.RefreshIndexAsync(isolated.Solution);
        var oldComment = baseline.Symbols!.Single(symbol => symbol.Id.EndsWith("Calculate(System.String)", StringComparison.Ordinal)).Documentation.Comments.Single();
        var newComment = actual.Symbols!.Single(symbol => symbol.Id.EndsWith("Calculate(System.String)", StringComparison.Ordinal)).Documentation.Comments.Single();

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal("incremental-body", actual.Refresh!.Mode);
        Assert.Equal(oldComment.RawComment, newComment.RawComment);
        Assert.Equal(oldComment.Location.Start.Line + 1, newComment.Location.Start.Line);
        Assert.NotEqual(oldComment.Id, newComment.Id);
    }

    [Fact]
    public async Task CancellationAtDocumentationCollectionPreservesThePublishedGeneration()
    {
        // Arrange
        var isolated = await PhysicalRefreshFixture.CreateAsync();
        var manifestBytes = await File.ReadAllBytesAsync(isolated.Manifest);
        var snapshot = await IndexStore.LoadSnapshotAsync(isolated.Manifest);
        var indexBytes = await File.ReadAllBytesAsync(snapshot.IndexPath);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(item => { if (item.Stage == IndexStage.CollectingDocumentation) cancellation.Cancel(); });

        // Act
        var failure = await Record.ExceptionAsync(() => SolutionAnalysis.RefreshIndexAsync(isolated.Solution, cancellation.Token, progress));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(isolated.Manifest));
        Assert.Equal(indexBytes, await File.ReadAllBytesAsync(snapshot.IndexPath));
        Assert.Equal(snapshot.Index.IndexId, (await IndexStore.LoadSnapshotAsync(isolated.Manifest)).Index.IndexId);
    }

    [Fact]
    public async Task SameLengthDocumentationEditRejectsStaleQueriesAndRebuildPublishesNewAuthorTextInOneGeneration()
    {
        // Arrange
        var file = Path.Combine(fixture.Root, "Library", "Calculator.cs");
        var original = await File.ReadAllTextAsync(file);
        var oldSource = original.Replace("    public static int Calculate(int value)",
            "    /// <summary>Original integer wording.</summary>\n    public static int Calculate(int value)", StringComparison.Ordinal);
        var newSource = oldSource.Replace("Original integer wording.", "Modified integer wording.", StringComparison.Ordinal);
        const string id = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)";
        LoadedSnapshot? oldSnapshot = null;
        try
        {
            await File.WriteAllTextAsync(file, oldSource);
            var initial = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);
            oldSnapshot = await IndexStore.PublishAsync(initial);
            var timestamp = File.GetLastWriteTimeUtc(file);
            var previousIndexBytes = await File.ReadAllBytesAsync(oldSnapshot.IndexPath);

            // Act
            await File.WriteAllTextAsync(file, newSource);
            File.SetLastWriteTimeUtc(file, timestamp);
            var staleStatus = await IndexStore.CheckFreshnessAsync(oldSnapshot.Index);
            var staleError = await Record.ExceptionAsync(() => DocumentationSnapshotQueries.GetAsync(oldSnapshot, id));
            var rebuilt = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);
            var published = await IndexStore.PublishAsync(rebuilt);
            var loaded = await IndexStore.LoadSnapshotAsync(published.Path);
            var current = await DocumentationSnapshotQueries.GetAsync(loaded, id);
            var replacedError = await Record.ExceptionAsync(() => DocumentationSnapshotQueries.GetAsync(loaded, id,
                expectedSnapshot: oldSnapshot.Index.IndexId));
            var oldBytesAfter = await File.ReadAllBytesAsync(oldSnapshot.IndexPath);

            // Assert
            Assert.Equal(oldSource.Length, newSource.Length);
            Assert.True(staleStatus.IsStale);
            Assert.Contains("Library/Calculator.cs", staleStatus.ChangedFiles);
            Assert.IsType<StaleIndexException>(staleError);
            Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(replacedError).Code);
            Assert.NotEqual(oldSnapshot.Index.IndexId, loaded.Index.IndexId);
            Assert.True(loaded.VerifiedGeneration);
            Assert.NotEqual(oldSnapshot.GenerationDirectory, loaded.GenerationDirectory);
            Assert.Equal("Modified integer wording.", Assert.Single(current.Documentation.Sections, s => s.Tag == "summary").Text);
            Assert.Equal("present", current.Documentation.Status);
            Assert.Equal(id, current.Id);
            Assert.Equal(previousIndexBytes, oldBytesAfter);
            Assert.Contains("Original integer wording.", Assert.Single(oldSnapshot.Index.Symbols!.Single(s => s.Id == id).Documentation.Comments).RawComment);
            Assert.Equal(oldSnapshot.Index.Methods, loaded.Index.Methods);
            foreach (var method in loaded.Index.Methods)
                Assert.Equal(oldSnapshot.Index.References[method.Id], loaded.Index.References[method.Id]);
        }
        finally
        {
            await File.WriteAllTextAsync(file, original);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task MissingDocumentationStaysMissingAfterDiskRoundTripAndSnapshotsNeverMixInheritedSources()
    {
        // Arrange
        var file = Path.Combine(fixture.Root, "Library", "DocumentationRefresh.cs");
        const string id = "Library/Library.csproj::M:Sample.DocDerived.Run";
        const string source = """
            namespace Sample;
            public class DocBase
            {
                /// <summary>First source wording.</summary>
                public virtual void Run() { }
                public void NoDescription() { }
            }
            public class DocDerived : DocBase
            {
                /// <inheritdoc/>
                public override void Run() { }
            }
            """;
        try
        {
            await File.WriteAllTextAsync(file, source);
            var beforeIndex = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);
            var before = await IndexStore.PublishAsync(beforeIndex);

            // Act
            await File.WriteAllTextAsync(file, source.Replace("First source wording.", "Later source wording.", StringComparison.Ordinal));
            var afterIndex = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);
            var after = await IndexStore.PublishAsync(afterIndex);
            var loaded = await IndexStore.LoadSnapshotAsync(after.Path);
            var inherited = await DocumentationSnapshotQueries.GetAsync(loaded, id);
            var missing = await DocumentationSnapshotQueries.GetAsync(loaded,
                "Library/Library.csproj::M:Sample.DocBase.NoDescription");
            var staleBefore = await Record.ExceptionAsync(() => DocumentationSnapshotQueries.GetAsync(before, id));

            // Assert
            Assert.Equal("Later source wording.", Assert.Single(inherited.Documentation.Sections, s => s.Tag == "summary").Text);
            Assert.Equal("Library/Library.csproj::M:Sample.DocBase.Run", Assert.Single(inherited.Documentation.Sections).Evidence.SourceSymbolId);
            Assert.Equal("missing", missing.Documentation.Status);
            Assert.Empty(missing.Documentation.Sections);
            Assert.Empty(missing.Documentation.Comments);
            Assert.IsType<StaleIndexException>(staleBefore);
            Assert.Equal(after.Index.IndexId, loaded.Index.IndexId);
            Assert.NotEqual(before.Index.IndexId, loaded.Index.IndexId);
        }
        finally
        {
            File.Delete(file);
            await fixture.RestorePublicationAsync();
        }
    }
}
