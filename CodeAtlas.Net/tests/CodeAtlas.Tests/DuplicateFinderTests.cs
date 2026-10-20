using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateFinderTests
{
    [Fact]
    public void RenamedIdentifiersStayTogetherAndADifferentBodyStaysOut()
    {
        // Arrange
        var bodies = new[]
        {
            ("a", "if (ready) { total = value + 1; return total; }"),
            ("b", "if (ready) { total = other + 1; return total; }"),
            ("c", "if (ready) { total = value + 2; return total; }")
        };

        // Act
        var catalog = DuplicateFinder.FromBodies(bodies);

        // Assert
        var exact = Assert.Single(catalog.Groups, group => group.Reason == "exact-normalized-body");
        Assert.Equal(new[] { "a", "b" }, exact.MemberIds);
        Assert.DoesNotContain(exact.MemberIds, id => id == "c");
    }

    [Fact]
    public void ASmallEditIsANearCandidateAndNotAnExactCopy()
    {
        // Arrange
        var bodies = new[]
        {
            ("left", "if (ready) { total = value + offset; return total; }"),
            ("right", "if (ready) { total = value + offset + 1; return total; }")
        };

        // Act
        var catalog = DuplicateFinder.FromBodies(bodies);

        // Assert
        Assert.DoesNotContain(catalog.Groups, group => group.Reason == "exact-normalized-body");
        var near = Assert.Single(catalog.Groups);
        Assert.Equal("near-normalized-body", near.Reason);
        Assert.Equal(new[] { "left", "right" }, near.MemberIds);
    }
}
