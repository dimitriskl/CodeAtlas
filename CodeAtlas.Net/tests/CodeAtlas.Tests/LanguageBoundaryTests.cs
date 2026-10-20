using Xunit;

namespace CodeAtlas.Tests;

public sealed class LanguageBoundaryTests
{
    [Fact]
    public void OnlyCSharpIsAdvertised()
    {
        // Arrange
        var requested = new[] { "csharp", "angular", "typescript", "sql" };

        // Act
        var advertised = requested.Where(LanguageBoundary.Advertises).ToArray();

        // Assert
        Assert.Equal(new[] { "csharp" }, advertised);
    }
}
