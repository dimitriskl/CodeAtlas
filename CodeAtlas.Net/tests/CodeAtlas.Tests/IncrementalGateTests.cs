using Xunit;

namespace CodeAtlas.Tests;

public sealed class IncrementalGateTests(SemanticSolution fixture) : IClassFixture<SemanticSolution>
{
    [Theory]
    [InlineData("unknown-version", "baseline_metadata_incompatible")]
    [InlineData("wrong-identity", "baseline_metadata_incompatible")]
    [InlineData("wrong-inputs", "baseline_metadata_incompatible")]
    [InlineData("null-projects", "incremental_metadata_invalid")]
    [InlineData("null-trees", "incremental_metadata_invalid")]
    [InlineData("duplicate-project", "incremental_metadata_invalid")]
    [InlineData("duplicate-tree", "incremental_metadata_invalid")]
    [InlineData("unknown-context", "semantic_context_unverified")]
    public void InvalidOptionalCachesAlwaysChooseObservableFullFallback(string mutation, string reason)
    {
        // Arrange
        var index = fixture.Index;
        var state = index.IncrementalState!;
        var first = state.Projects[0];
        var mutated = mutation switch
        {
            "unknown-version" => state with { Version = 888 },
            "wrong-identity" => state with { IndexId = Guid.NewGuid().ToString("N") },
            "wrong-inputs" => state with { InputManifestHash = new string('0', 64) },
            "null-projects" => state with { Projects = null! },
            "null-trees" => state with { Projects = [first with { Trees = null! }] },
            "duplicate-project" => state with { Projects = state.Projects.Append(first).ToArray() },
            "duplicate-tree" => state with { Projects = [first with { Trees = first.Trees.Append(first.Trees[0]).ToArray() }] },
            _ => state with { UnsupportedReasons = ["Uninspectable custom semantic input"] }
        };

        // Act
        var actual = IncrementalGate.Decide(index, mutated, state, index.InputManifest!, index.Solution);

        // Assert
        Assert.False(actual.CanReuse);
        Assert.Equal(reason, actual.FallbackReason);
    }

    [Fact]
    public void AChangedCompilationTreeOrderConservativelyRequiresFullReferences()
    {
        // Arrange
        var index = fixture.Index;
        var state = index.IncrementalState!;
        var project = state.Projects.First(project => project.Trees.Length > 1);
        var reordered = project.Trees.Reverse().ToArray();
        var current = state with
        {
            Projects = state.Projects.Select(item => ReferenceEquals(item, project)
                ? item with { Trees = reordered } : item).ToArray()
        };

        // Act
        var actual = IncrementalGate.Decide(index, state, current, index.InputManifest!, index.Solution);

        // Assert
        Assert.False(actual.CanReuse);
        Assert.Equal("document_inventory_changed", actual.FallbackReason);
    }

    [Fact]
    public void PartialCoverageAloneDoesNotPreventValidatedContextReuse()
    {
        // Arrange
        var index = fixture.Index;

        // Act
        var actual = IncrementalGate.Decide(index, index.IncrementalState, index.IncrementalState!, index.InputManifest!, index.Solution);

        // Assert
        Assert.Equal("partial", index.InputManifest!.Coverage.Status);
        Assert.Empty(index.IncrementalState!.UnsupportedReasons);
        Assert.True(actual.CanReuse);
        Assert.Empty(actual.ChangedTrees);
    }
}
