using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateFinderTests
{
    [Fact]
    public void UnboundBodyPreviewPreservesIdentifiersAndLiteralSpelling()
    {
        // Arrange
        var bodies = new[] { ("a", "return Save(\"a\\\\b\");"), ("b", "return Delete(\"a  b\");") };

        // Act
        var catalog = DuplicateFinder.FromBodies(bodies);

        // Assert
        Assert.Empty(catalog.Groups);
        Assert.Equal(1,catalog.Version);
        Assert.Contains(catalog.Limitations, text => text.Contains("Unbound lexical preview",StringComparison.Ordinal));
    }

    [Fact]
    public void UnboundBodyPreviewCanOnlyGroupLexicallyIdenticalTokens()
    {
        // Arrange
        var bodies = new[] { ("a", "return value + 2;"), ("b", "return value + 2;"), ("c", "return input + 2;") };

        // Act
        var catalog = DuplicateFinder.FromBodies(bodies);

        // Assert
        var group=Assert.Single(catalog.Groups);
        Assert.Equal("exact-lexical-body",group.Reason);
        Assert.Equal(new[] {"a","b"},group.MemberIds);
    }
}
