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
}
