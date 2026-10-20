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
            ("a", "return value + 1;"),
            ("b", "return other + 1;"),
            ("c", "return value + 2;")
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
            ("left", "if (ready) { return value + total; }"),
            ("right", "if (ready) { return value + total + 1; }")
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
