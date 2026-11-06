using Xunit;

namespace CodeAtlas.Tests;

public sealed class CallerTraversalTests
{
    [Fact]
    public void BoundedTraversalReportsCallersCyclesAndStopsAtTheLimit()
    {
        // Arrange
        var catalog = new StructuralCatalog(StructuralCoverage.Current,
        [
            Edge("a", "b"),
            Edge("c", "b"),
            Edge("b", "a")
        ], []);

        // Act
        var callers = Callers.Traverse(catalog, "b", depth: 1, limit: 10);
        var deeper = Callers.Traverse(catalog, "b", depth: 2, limit: 10);
        var limited = Callers.Traverse(catalog, "b", depth: 2, limit: 1);

        // Assert
        Assert.Equal(new[] { "a", "c" }, callers.Edges.Select(edge => edge.CallerId).ToArray());
        Assert.Empty(callers.Cycles);
        Assert.Contains(deeper.Cycles, cycle => cycle == "b" || cycle == "a");
        Assert.Single(limited.Edges);
        Assert.Contains("potentially affected", callers.Limitations[0]);
    }

    private static StructuralFact Edge(string caller, string callee) =>
        new(caller, "resolved-invocation", new(callee, callee + "()", null, callee, null, "indexed-source"),
            new("roslyn", new(new(caller + ".cs", 1, 1), new(caller + ".cs", 1, 8)), "explicit-source-binding"));

    [Fact]
    public void DiamondAndRepeatedCallSitesAreNotCyclesAndEvidenceIsDeterministic()
    {
        // Arrange
        var first = Edge("a", "b");
        var second = first with { Evidence = first.Evidence with { Location = new(new("a.cs", 2, 1), new("a.cs", 2, 8)) } };
        var facts = new[] { Edge("b", "d"), Edge("c", "d"), Edge("a", "c"), second, first };
        var catalog = new StructuralCatalog(StructuralCoverage.Current, facts, []);
        var reversed = catalog with { Facts = facts.Reverse().ToArray() };

        // Act
        var result = Callers.Traverse(catalog, "d", depth: 3, limit: 100);
        var other = Callers.Traverse(reversed, "d", depth: 3, limit: 100);

        // Assert
        Assert.Empty(result.Cycles);
        Assert.Equal(5, result.Edges.Length);
        Assert.Equal(result.Edges, other.Edges);
    }

    [Fact]
    public void CycleOnAnAlternateDiamondPathIsDetected()
    {
        // Arrange
        var catalog = new StructuralCatalog(StructuralCoverage.Current,
            [Edge("b", "d"), Edge("c", "d"), Edge("a", "b"), Edge("a", "c"), Edge("c", "a")], []);

        // Act
        var result = Callers.Traverse(catalog, "d", depth: 5, limit: 100);

        // Assert
        Assert.NotEmpty(result.Cycles);
        Assert.DoesNotContain("d", result.Cycles);
        Assert.DoesNotContain("b", result.Cycles);
    }

    [Fact]
    public void ExactLimitIsCompleteAndAdditionalEdgesOrDepthAreExplicitlyTruncated()
    {
        // Arrange
        var catalog = new StructuralCatalog(StructuralCoverage.Current, [Edge("a", "b"), Edge("c", "a")], []);
        var isolated = catalog with { Facts = [Edge("a", "b")] };

        // Act
        var complete = Callers.Traverse(isolated, "b", depth: 1, limit: 1);
        var limited = Callers.Traverse(catalog, "b", depth: 2, limit: 1);
        var shallow = Callers.Traverse(catalog, "b", depth: 1, limit: 100);
        var empty = Callers.Traverse(catalog, "unknown", depth: 1, limit: 100);

        // Assert
        Assert.False(complete.HasMore);
        Assert.False(complete.Truncated);
        Assert.True(complete.CyclesComplete);
        Assert.True(limited.HasMore);
        Assert.True(limited.Truncated);
        Assert.False(limited.CyclesComplete);
        Assert.True(shallow.DepthLimited);
        Assert.True(shallow.Truncated);
        Assert.Empty(empty.Edges);
        Assert.False(empty.Truncated);
    }

    [Fact]
    public void RepeatedEvidenceCannotExhaustUnboundedTraversalWork()
    {
        // Arrange
        var fact = Edge("a", "b");
        var catalog = new StructuralCatalog(StructuralCoverage.Current,
            Enumerable.Repeat(fact, Callers.MaximumEdgesExamined + 1).ToArray(), []);

        // Act
        var result = Callers.Traverse(catalog, "b", depth: 5, limit: 1000);

        // Assert
        Assert.Single(result.Edges);
        Assert.Empty(result.Cycles);
        Assert.True(result.WorkLimited);
        Assert.True(result.Truncated);
        Assert.False(result.CyclesComplete);
        Assert.Equal(Callers.MaximumEdgesExamined, result.EdgesExamined);
        Assert.Equal(catalog.Facts.Length, result.FactsScanned);
    }

    [Fact]
    public void CatalogScanCapExplicitlyReportsIncompleteLookupCoverage()
    {
        // Arrange
        var fact = Edge("a", "b");
        var catalog = new StructuralCatalog(StructuralCoverage.Current,
            Enumerable.Repeat(fact, Callers.MaximumFactsScanned + 1).ToArray(), []);

        // Act
        var result = Callers.Traverse(catalog, "outside-scanned-graph", depth: 5, limit: 1000);

        // Assert
        Assert.Empty(result.Edges);
        Assert.True(result.WorkLimited);
        Assert.True(result.Truncated);
        Assert.False(result.CyclesComplete);
        Assert.Equal(Callers.MaximumFactsScanned, result.FactsScanned);
        Assert.Equal(0, result.EdgesExamined);
    }
}
