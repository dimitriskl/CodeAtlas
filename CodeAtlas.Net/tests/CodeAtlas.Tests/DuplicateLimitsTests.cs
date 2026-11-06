using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateLimitsTests
{
    public static IEnumerable<object[]> IntegerBounds()
    {
        (string Property, int Minimum, int Maximum)[] bounds =
        [
            (nameof(DuplicateLimits.MaximumMethods), 1, 100_000),
            (nameof(DuplicateLimits.MaximumTokensPerBody), 24, 4096),
            (nameof(DuplicateLimits.MaximumTotalTokens), 1, 2_000_000),
            (nameof(DuplicateLimits.MaximumCatalogBytes), 1024, 67_108_864),
            (nameof(DuplicateLimits.MinimumTokens), 1, 4096),
            (nameof(DuplicateLimits.MaximumPosting), 2, 64),
            (nameof(DuplicateLimits.MaximumPairVisits), 1, 500_000),
            (nameof(DuplicateLimits.MaximumComparisons), 1, 100_000),
            (nameof(DuplicateLimits.MaximumFindings), 1, 10_000),
            (nameof(DuplicateLimits.MaximumShinglesPerBody), 1, 64),
            (nameof(DuplicateLimits.MaximumPostingKeys), 1, 250_000),
            (nameof(DuplicateLimits.MaximumPostingEntries), 1, 500_000),
            (nameof(DuplicateLimits.MaximumScanBytes), 1024, 16_777_216)
        ];
        foreach (var (property, minimum, maximum) in bounds)
        {
            yield return [property, minimum - 1, false];
            yield return [property, minimum, true];
            yield return [property, maximum, true];
            yield return [property, maximum + 1, false];
        }
    }

    [Theory]
    [MemberData(nameof(IntegerBounds))]
    public void IntegerBudgetsKeepTheirDocumentedInclusiveBounds(string property, int value, bool accepted)
    {
        // Arrange
        var limits = new DuplicateLimits();
        typeof(DuplicateLimits).GetProperty(property)!.SetValue(limits, value);

        // Act
        var failure = Record.Exception(limits.Validate);

        // Assert
        if (accepted) Assert.Null(failure);
        else Assert.Equal("Duplicate limits are outside the supported bounds.", Assert.IsType<ArgumentException>(failure).Message);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(1.0, true)]
    [InlineData(0.49, false)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void NearScoreRequiresAFiniteValueWithinTheInclusiveRange(double value, bool accepted)
    {
        // Arrange
        var limits = new DuplicateLimits(MinimumNearScore: value);

        // Act
        var failure = Record.Exception(limits.Validate);

        // Assert
        if (accepted) Assert.Null(failure);
        else Assert.Equal("Duplicate limits are outside the supported bounds.", Assert.IsType<ArgumentException>(failure).Message);
    }
}
