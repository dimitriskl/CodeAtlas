using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateIntegrityFixture : IAsyncLifetime
{
    private SqliteFileUpdateTests.SampleCopy copy = null!;
    public CodebaseIndex Index { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        copy = await SqliteFileUpdateTests.SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(copy.Calculator)!, "DuplicateCases.cs"), """
            public static class DuplicateCases {
                /// <summary>Scale the supplied value after adding an offset.</summary>
                public static int A(int value) { int total=value+2; int scaled=total*3; if(total>10) return scaled; return value; }
                /// <summary>Scale the supplied value after adding an offset.</summary>
                public static int B(int input) { int result=input+2; int multiplied=result*3; if(result>10) return multiplied; return input; }
                public static T Unsupported<T>(T item) => item;
            """ + string.Join("\n", Enumerable.Range(0, 10).Select(number => $"public static int Noise{number}() => {number};")) + "\n}");
        Index = await SolutionAnalysis.CreateIndexAsync(copy.Solution);
    }

    public Task DisposeAsync() { copy.Dispose(); return Task.CompletedTask; }
}

public sealed class DuplicateValidationTests(DuplicateIntegrityFixture fixture) : IClassFixture<DuplicateIntegrityFixture>
{
    [Theory]
    [InlineData("valid")]
    [InlineData("difference-kind")]
    [InlineData("difference-left-offset")]
    [InlineData("difference-right-offset")]
    [InlineData("difference-right-text")]
    [InlineData("candidate-score")]
    [InlineData("candidate-source")]
    [InlineData("work-count")]
    [InlineData("vocabulary")]
    [InlineData("body-offset")]
    [InlineData("normalized-hash")]
    [InlineData("coverage")]
    [InlineData("group-evidence")]
    [InlineData("omission-reason")]
    [InlineData("legacy-publication")]
    [InlineData("legacy-null-groups")]
    [InlineData("legacy-null-members")]
    [InlineData("structural-owner")]
    [InlineData("structural-kind")]
    [InlineData("structural-resolution")]
    [InlineData("structural-source")]
    [InlineData("structural-evidence")]
    [InlineData("structural-coverage")]
    [InlineData("structural-issue")]
    [InlineData("structural-valid-issue")]
    [InlineData("structural-null-fact")]
    [InlineData("structural-null-target")]
    [InlineData("structural-null-evidence")]
    [InlineData("index-null-warnings")]
    [InlineData("index-missing-id")]
    [InlineData("index-invalid-reference")]
    [InlineData("documentation-issue-valid")]
    [InlineData("documentation-issue-code")]
    [InlineData("documentation-issue-message")]
    [InlineData("documentation-issue-path")]
    public async Task DuplicateCatalogIntegrityAcceptsValidEvidenceAndRejectsCorruption(string corruption)
    {
        // Arrange
        var index = SnapshotTestIdentity.NewGeneration(fixture.Index);
        var catalog = index.Duplicates!;
        var analysis = catalog.Analysis!;
        var scan = catalog.Scan!;
        var pair = scan.Candidates.Single(candidate => candidate.Left.SymbolId.Contains("DuplicateCases.A(", StringComparison.Ordinal)
            && candidate.Right.SymbolId.Contains("DuplicateCases.B(", StringComparison.Ordinal));
        var difference = pair.ImportantDifferences.First();
        var body = analysis.Bodies.Single(item => item.SymbolId == pair.Left.SymbolId);
        var changedPair = corruption switch
        {
            "difference-kind" => pair with { ImportantDifferences = [difference with { Kind = " " }] },
            "difference-left-offset" => pair with { ImportantDifferences = [difference with { LeftTokenOffset = int.MaxValue }] },
            "difference-right-offset" => pair with { ImportantDifferences = [difference with { RightTokenOffset = int.MaxValue }] },
            "difference-right-text" => pair with { ImportantDifferences = [difference with { Right = null! }] },
            "candidate-score" => pair with { Score = double.NaN },
            "candidate-source" => pair with { Left = pair.Left with { FileHash = new string('0', 64) } },
            _ => pair
        };
        var changedBody = corruption switch
        {
            "body-offset" => body with { TokenOffsets = body.TokenOffsets.Select(_ => int.MaxValue).ToArray() },
            "normalized-hash" => body with { NormalizedHash = new string('0', 64) },
            _ => body
        };
        var omissions = corruption == "omission-reason"
            ? new[] { new DuplicateOmission(body.SymbolId, " ", body.Location) } : analysis.Omissions;
        var altered = catalog with
        {
            Groups = corruption == "group-evidence" ? catalog.Groups.Select(group => group with { Reason = "wrong" }).ToArray() : catalog.Groups,
            Analysis = analysis with
            {
                Vocabulary = corruption == "vocabulary" ? analysis.Vocabulary.Select(_ => " ").ToArray() : analysis.Vocabulary,
                Bodies = analysis.Bodies.Select(item => item == body ? changedBody : item).ToArray(), Omissions = omissions,
                Coverage = corruption == "coverage" ? analysis.Coverage with { Supported = [] } : analysis.Coverage
            },
            Scan = scan with
            {
                Candidates = scan.Candidates.Select(item => item == pair ? changedPair : item).ToArray(), Omissions = omissions,
                Work = corruption == "work-count" ? scan.Work with { RetainedFindings = scan.Work.RetainedFindings + 1 } : scan.Work
            }
        };
        if (corruption.StartsWith("legacy-", StringComparison.Ordinal))
            altered = altered with { Version = 1, Analysis = null, Scan = null,
                Groups = corruption == "legacy-null-groups" ? null! : corruption == "legacy-null-members"
                    ? altered.Groups.Select(group => group with { MemberIds = null! }).ToArray() : altered.Groups };
        var structural = index.StructuralCatalog!;
        var fact = structural.Facts.First();
        var changedFact = corruption switch
        {
            "structural-owner" => fact with { OwnerId = "missing" },
            "structural-kind" => fact with { Kind = "unknown" },
            "structural-resolution" => fact with { Target = fact.Target with { Resolution = "unknown" } },
            "structural-source" => fact with { Target = fact.Target with { Resolution = "indexed-source", SourceSymbolId = "missing" } },
            "structural-evidence" => fact with { Evidence = fact.Evidence with { Origin = "text-search" } },
            "structural-null-fact" => null!,
            "structural-null-target" => fact with { Target = null! },
            "structural-null-evidence" => fact with { Evidence = null! },
            _ => fact
        };
        var changedStructural = structural with
        {
            Facts = structural.Facts.Select(item => item == fact ? changedFact : item).ToArray(),
            Coverage = corruption == "structural-coverage" ? structural.Coverage with { Exclusions = [] } : structural.Coverage,
            Issues = corruption is "structural-issue" or "structural-valid-issue"
                ? [new(corruption == "structural-issue" ? "missing" : fact.OwnerId, "unavailable", fact.Evidence.Location)] : structural.Issues
        };
        var published = index with { Duplicates = altered, StructuralCatalog = changedStructural };
        if (corruption == "index-null-warnings") published = published with { Warnings = null! };
        if (corruption == "index-missing-id") published = published with { IndexId = " " };
        if (corruption == "index-invalid-reference") published = published with { References = [] };
        if (corruption.StartsWith("documentation-issue-", StringComparison.Ordinal))
        {
            var inventory = index.Symbols!;
            var selected = inventory.First();
            var issue = new DocumentationIssue(corruption == "documentation-issue-code" ? " " : "unresolved",
                corruption == "documentation-issue-message" ? " " : "Unresolved documentation target.",
                corruption == "documentation-issue-path" ? [" "] : [selected.Id]);
            published = published with { Symbols = inventory.Select(symbol => symbol == selected
                ? symbol with { Documentation = symbol.Documentation with { Status = "unresolved", Issues = [issue] } } : symbol).ToArray() };
        }

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.SaveAsync(published));

        // Assert
        if (corruption is "valid" or "structural-valid-issue" or "documentation-issue-valid") Assert.Null(failure);
        else Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
        if (corruption == "legacy-publication")
            Assert.Equal("Publication requires the current duplicate catalog. Rebuild the analysis instead of republishing legacy candidates.", failure!.Message);
        if (corruption.StartsWith("index-", StringComparison.Ordinal))
            Assert.Equal("Invalid index contents. Rebuild the index.", failure!.Message);
    }

    [Theory]
    [InlineData("left", true)]
    [InlineData("right", true)]
    [InlineData("other-project", false)]
    public void DuplicateQueriesFilterEitherSideAndProject(string selection, bool expected)
    {
        // Arrange
        var index = fixture.Index;
        var catalog = index.Duplicates!;
        var pair = catalog.Scan!.Candidates.Single(candidate => candidate.Left.SymbolId.Contains("DuplicateCases.A(", StringComparison.Ordinal)
            && candidate.Right.SymbolId.Contains("DuplicateCases.B(", StringComparison.Ordinal));
        var id = selection == "right" ? pair.Right.SymbolId : pair.Left.SymbolId;
        var request = new QueryRequest(Project: selection == "other-project" ? "missing" : pair.Left.Project);

        // Act
        var result = DuplicateSnapshotQueries.FindGroups(catalog, index.IndexId, request, id);

        // Assert
        if (expected) Assert.Equal(pair.PairId, Assert.Single(result.Page.Items).Hash);
        else Assert.Empty(result.Page.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PurposeCandidatesKeepTheRetainedStructuralEvidenceFromEitherSide(bool reverse)
    {
        // Arrange
        var index = fixture.Index;
        var catalog = index.Duplicates!;
        var pair = catalog.Scan!.Candidates.Single(candidate => candidate.Left.SymbolId.Contains("DuplicateCases.A(", StringComparison.Ordinal)
            && candidate.Right.SymbolId.Contains("DuplicateCases.B(", StringComparison.Ordinal));
        var selected = reverse ? pair.Right.SymbolId : pair.Left.SymbolId;
        var other = reverse ? pair.Left.SymbolId : pair.Right.SymbolId;

        // Act
        var result = PurposeOverlap.Build(index.Symbols!, index.StructuralCatalog!, catalog, index.IndexId, selected, new QueryRequest());

        // Assert
        Assert.Contains(result.Page.Items, candidate => candidate.Right.SymbolId == other
            && candidate.ImportantDifferences.Any(text => text.Contains(pair.PairId, StringComparison.Ordinal)));
    }
}

public sealed class DuplicateLivePublicationTests
{
    [Fact]
    public async Task LiveEditPersistsDuplicateBodiesFindingsAndOmissionsLikeAFullRebuild()
    {
        // Arrange
        var fixture = new DuplicateIntegrityFixture();
        await fixture.InitializeAsync();
        try
        {
            var index = fixture.Index;
            await IndexStore.SaveAsync(index);
            using var session = await LiveIndexSession.OpenAsync(index.Solution);
            var path = Path.Combine(Path.GetDirectoryName(index.Solution)!, "Library", "DuplicateCases.cs");
            var original = await File.ReadAllTextAsync(path);
            var changed = original.Replace("input+2", "input+7", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, changed);
            var rebuilt = await SolutionAnalysis.CreateIndexAsync(index.Solution);
            await File.WriteAllTextAsync(path, original);

            // Act
            await session.ApplyAsync(path, changed);

            // Assert
            var stored = await IndexStore.LoadAsync(IndexStore.DefaultPath(index.Solution));
            Assert.NotEmpty(stored.Duplicates!.Scan!.Candidates);
            Assert.NotEmpty(stored.Duplicates.Analysis!.Omissions);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(rebuilt.Duplicates!.Analysis),
                System.Text.Json.JsonSerializer.Serialize(stored.Duplicates.Analysis));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(rebuilt.Duplicates.Scan! with { IndexId = stored.IndexId }),
                System.Text.Json.JsonSerializer.Serialize(stored.Duplicates.Scan));
        }
        finally { await fixture.DisposeAsync(); }
    }
}
